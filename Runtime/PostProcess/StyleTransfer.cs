using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Unity.Barracuda;

namespace JTRP.PostProcess
{
	public enum ModelType
	{
		Reference        = 0,
		RefBut32Channels = 1
	}

	[Serializable]
	public class WorkerFactoryTypeParameter : VolumeParameter<WorkerFactory.Type>
	{
		public WorkerFactoryTypeParameter(WorkerFactory.Type value, bool overrideState = false)
			: base(value, overrideState) { }
	}

	[Serializable]
	public class StyleTexturesParameter : VolumeParameter<List<Texture2D>>
	{
		public StyleTexturesParameter(List<Texture2D> value, bool overrideState = false)
			: base(value, overrideState) { }
	}

	[Serializable, VolumeComponentMenu("JTRP/StyleTransfer")]
	public class StyleTransfer : CustomPostProcessVolumeComponent, IPostProcessComponent
	{
		public Vector2Parameter                   inputResolution                = new Vector2Parameter(new Vector2Int(960, 540));
		public BoolParameter                      forceBilinearUpsample2DInModel = new BoolParameter(true);
		public WorkerFactoryTypeParameter         workerType                     = new WorkerFactoryTypeParameter(WorkerFactory.Type.Auto);
		public BoolParameter                      debugModelLoading              = new BoolParameter(false);
		public CloudLayerEnumParameter<ModelType> modelType                      = new CloudLayerEnumParameter<ModelType>(ModelType.RefBut32Channels);
		public ClampedFloatParameter              styleTextureIndex              = new ClampedFloatParameter(0.0f, 0.0f, 1.0f - 1e-5f);
		public StyleTexturesParameter             styleTextures                  = new StyleTexturesParameter(null);

		public override CustomPostProcessInjectionPoint injectionPoint => CustomPostProcessInjectionPoint.BeforePostProcess;

		// The compiled model used for performing inference
		private Model _model;

		// The interface used to execute the neural network
		private IWorker _worker;

		private          RTHandle      _rtHandle;
		private          RenderTexture _rTex;
		private          NNModel[]     _nnModels;
		private          List<float[]> _predictionAlphasBetasData;
		private          Texture2D     _lastStyle;
		private          Tensor        _input;
		private          Tensor        _pred;
		private readonly Vector4       _postNetworkColorBias = new Vector4(0.4850196f, 0.4579569f, 0.4076039f, 0.0f);
		private          List<string>  _layerNameToPatch;

		public bool IsActive() => styleTextures.value != null && styleTextures.value.Count > 0;

		private Texture2D _styleTexture =>
			IsActive() ? styleTextures.value[Mathf.FloorToInt(styleTextureIndex.value * styleTextures.value.Count)] : null;

		public override void Setup()
		{
			Debug.Log("Setup");

			// Load assets from Resources folder
			_nnModels = new NNModel[]
			{
				Resources.Load<NNModel>("adele_2"),
				Resources.Load<NNModel>("model_32channels")
			};

			Debug.Assert(Enum.GetNames(typeof(ModelType)).Length == _nnModels.Length);
			Debug.Assert(_nnModels.All(m => m != null));

			ComputeInfo.channelsOrder = ComputeInfo.ChannelsOrder.NCHW;

			// Compile the model asset into an object oriented representation
			_model = ModelLoader.Load(_nnModels[(int)modelType.value], debugModelLoading.value);

			float scale = 1; //inputResolution.value.y / (float)Screen.height;

			_rtHandle = RTHandles.Alloc(
										scaleFactor: Vector2.one * scale,
										filterMode: FilterMode.Point,
										wrapMode: TextureWrapMode.Clamp,
										enableRandomWrite: true
									   );
			_rTex = RenderTexture.GetTemporary(_rtHandle.rt.width, _rtHandle.rt.height, 24, RenderTextureFormat.ARGBHalf);

			//Prepare style transfer prediction and runtime worker at load time (to avoid memory allocation at runtime)
			PrepareStylePrediction();
			CreateBarracudaWorker();
		}

		public override void Render(CommandBuffer cmd, HDCamera camera, RTHandle source, RTHandle destination)
		{
			if (!IsActive() || _styleTexture == null) return;
			if (_lastStyle != _styleTexture)
			{
				_lastStyle = _styleTexture;
				PrepareStylePrediction();
				PatchRuntimeWorkerWithStylePrediction();
			}

			cmd.Blit(source, _rtHandle, 0, 0);
			_input = new Tensor(_rtHandle, 3);
			Dictionary<string, Tensor> temp = new Dictionary<string, Tensor>();
			temp.Add("frame", _input);
			_worker.Execute(temp);
			_pred = _worker.PeekOutput();
			_input.Dispose();
			_pred.ToRenderTexture(_rTex, 0, 0, Vector4.one, _postNetworkColorBias);
			_pred.Dispose();

			cmd.Blit(_rTex, destination);
		}

		public override void Cleanup()
		{
			Debug.Log("Cleanup");
			_lastStyle = null;
			if (_worker != null)
				_worker.Dispose();
			if (_rtHandle != null)
				_rtHandle.Release();
			if (_rTex != null)
				_rTex.Release();
			if (_input != null)
				_input.Dispose();
			if (_pred != null)
				_pred.Dispose();
		}

		// https://github.com/JasonMa0012/barracuda-style-transfer/blob/fb61d2d8172e3150f6ebbfb78443d0fe9def66db/Assets/BarracudaStyleTransfer/BarracudaStyleTransfer.cs#L575
		private void PrepareStylePrediction()
		{
			if (_styleTexture == null) return;

			Model tempModel = ModelLoader.Load(_nnModels[(int)modelType.value], debugModelLoading.value); //_model.ShallowCopy();
			List<Layer> predictionAlphasBetas = new List<Layer>();
			List<Layer> layerList = new List<Layer>(tempModel.layers);

			// Remove Divide by 255, Unity textures are in [0, 1] already
			int firstDivide = FindLayerIndexByName(layerList, "Style_Prediction_Network/normalized_image");
			if (firstDivide < 0)
				Debug.Log(0);
			layerList[firstDivide + 1].inputs[0] = layerList[firstDivide].inputs[0];
			layerList.RemoveAt(firstDivide);

			// Pre-process network to get it to run and extract Style alpha/beta tensors
			Layer lastConv = null;
			for (int i = 0; i < layerList.Count; i++)
			{
				Layer layer = layerList[i];

				// Remove Mirror padding layers (not supported, TODO)
				if (layer.name.Contains("reflect_padding"))
				{
					layerList[i + 1].inputs = layer.inputs;
					layerList[i + 1].pad = layer.pad.ToArray();
					layerList.RemoveAt(i);
					i--;
					continue;
				}
				// Placeholder instance norm bias + scale tensors
				if (layer.type == Layer.Type.Conv2D || layer.type == Layer.Type.Conv2DTrans)
				{
					lastConv = layer;
				}
				else if (layer.type == Layer.Type.Normalization)
				{
					int channels = lastConv.datasets[1].shape.channels;
					layer.datasets = new Layer.DataSet[2];

					layer.datasets[0].shape = new TensorShape(1, 1, 1, channels);
					layer.datasets[0].offset = 0;
					layer.datasets[0].length = channels;

					layer.datasets[1].shape = new TensorShape(1, 1, 1, channels);
					layer.datasets[1].offset = channels;
					layer.datasets[1].length = channels;

					float[] data = new float[channels * 2];
					for (int j = 0; j < data.Length / 2; j++)
						data[j] = 1.0f;
					for (int j = data.Length / 2; j < data.Length; j++)
						data[j] = 0.0f;
					layer.weights = new BarracudaArrayFromManagedArray(data);
				}

				if (layer.type != Layer.Type.StridedSlice && layer.name.Contains("StyleNetwork/"))
				{
					layerList.RemoveAt(i);
					i--;
				}

				if (layer.type == Layer.Type.StridedSlice)
				{
					predictionAlphasBetas.Add(layer);
				}
			}
			tempModel.layers = layerList;
			// Run Style_Prediction_Network on given style
			var styleInput = new Tensor(_styleTexture);
			Dictionary<string, Tensor> temp = new Dictionary<string, Tensor>();
			temp.Add("frame", styleInput);
			temp.Add("style", styleInput);
			IWorker tempWorker = WorkerFactory.CreateWorker(WorkerFactory.ValidateType(workerType.value), tempModel, debugModelLoading.value);
			tempWorker.Execute(temp);

			// Store alpha/beta tensors from Style_Prediction_Network to feed into the run-time network
			_predictionAlphasBetasData = new List<float[]>();
			foreach (var layer in predictionAlphasBetas)
			{
				_predictionAlphasBetasData.Add(tempWorker.PeekOutput(layer.name).ToReadOnlyArray());
			}

			tempWorker.Dispose();
			styleInput.Dispose();
		}

		private void CreateBarracudaWorker()
		{
			int savedAlphaBetasIndex = 0;
			_layerNameToPatch = new List<string>();
			List<Layer> layerList = new List<Layer>(_model.layers);

			// Pre-process Network for run-time use
			Layer lastConv = null;
			for (int i = 0; i < layerList.Count; i++)
			{
				Layer layer = layerList[i];

				// Remove Style_Prediction_Network: constant with style, executed once in Setup()
				if (layer.name.Contains("Style_Prediction_Network/"))
				{
					layerList.RemoveAt(i);
					i--;
					continue;
				}

				// Fix Upsample2D size parameters
				if (layer.type == Layer.Type.Upsample2D)
				{
					layer.pool = new[] { 2, 2 };
					//ref model is supposed to be nearest sampling but bilinear scale better when network is applied at lower resoltions
					bool useBilinearUpsampling = forceBilinearUpsample2DInModel.value || (modelType.value != ModelType.Reference);
					layer.axis = useBilinearUpsampling ? 1 : -1;
				}

				// Remove Mirror padding layers (not supported, TODO)
				if (layer.name.Contains("reflect_padding"))
				{
					layerList[i + 1].inputs = layer.inputs;
					layerList[i + 1].pad = layer.pad.ToArray();
					layerList.RemoveAt(i);
					i--;
				}
				else if (layer.type == Layer.Type.Conv2D || layer.type == Layer.Type.Conv2DTrans)
				{
					lastConv = layer;
				}
				else if (layer.type == Layer.Type.Normalization)
				{
					// Manually set alpha/betas from Style_Prediction_Network as scale/bias tensors for InstanceNormalization
					if (layerList[i - 1].type == Layer.Type.StridedSlice)
					{
						int channels = _predictionAlphasBetasData[savedAlphaBetasIndex].Length;
						layer.datasets = new Layer.DataSet[2];

						layer.datasets[0].shape = new TensorShape(1, 1, 1, channels);
						layer.datasets[0].offset = 0;
						layer.datasets[0].length = channels;

						layer.datasets[1].shape = new TensorShape(1, 1, 1, channels);
						layer.datasets[1].offset = channels;
						layer.datasets[1].length = channels;

						_layerNameToPatch.Add(layer.name);

						float[] data = new float[channels * 2];
						for (int j = 0; j < data.Length / 2; j++)
							data[j] = _predictionAlphasBetasData[savedAlphaBetasIndex][j];
						for (int j = data.Length / 2; j < data.Length; j++)
							data[j] = _predictionAlphasBetasData[savedAlphaBetasIndex + 1][j - data.Length / 2];

						layer.weights = new BarracudaArrayFromManagedArray(data);

						savedAlphaBetasIndex += 2;
					}
					// Else initialize scale/bias tensors of InstanceNormalization to default 1/0
					else
					{
						int channels = lastConv.datasets[1].shape.channels;
						layer.datasets = new Layer.DataSet[2];

						layer.datasets[0].shape = new TensorShape(1, 1, 1, channels);
						layer.datasets[0].offset = 0;
						layer.datasets[0].length = channels;

						layer.datasets[1].shape = new TensorShape(1, 1, 1, channels);
						layer.datasets[1].offset = channels;
						layer.datasets[1].length = channels;

						float[] data = new float[channels * 2];
						for (int j = 0; j < data.Length / 2; j++)
							data[j] = 1.0f;
						for (int j = data.Length / 2; j < data.Length; j++)
							data[j] = 0.0f;
						layer.weights = new BarracudaArrayFromManagedArray(data);
					}
				}
			}

			// Remove Slice layers originally used to get alpha/beta tensors into Style_Network
			for (int i = 0; i < layerList.Count; i++)
			{
				Layer layer = layerList[i];
				if (layer.type == Layer.Type.StridedSlice)
				{
					layerList.RemoveAt(i);
					i--;
				}
			}

			// Fold Relu into instance normalisation
			Dictionary<string, string> reluToInstNorm = new Dictionary<string, string>();
			for (int i = 0; i < layerList.Count; i++)
			{
				Layer layer = layerList[i];
				if (layer.type == Layer.Type.Activation && layer.activation == Layer.Activation.Relu)
				{
					if (layerList[i - 1].type == Layer.Type.Normalization)
					{
						layerList[i - 1].activation = layer.activation;
						reluToInstNorm[layer.name] = layerList[i - 1].name;
						layerList.RemoveAt(i);
						i--;
					}
				}
			}
			for (int i = 0; i < layerList.Count; i++)
			{
				Layer layer = layerList[i];
				for (int j = 0; j < layer.inputs.Length; j++)
				{
					if (reluToInstNorm.ContainsKey(layer.inputs[j]))
					{
						layer.inputs[j] = reluToInstNorm[layer.inputs[j]];
					}
				}
			}

			// Feed first convolution directly with input (no need for normalisation from the model)
			string firstConvName = "StyleNetwork/conv1/convolution_conv1/convolution";
			int firstConv = FindLayerIndexByName(layerList, firstConvName);
			layerList[firstConv].inputs = new[] { _model.inputs[1].name };

			if (modelType.value == ModelType.Reference)
			{
				layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/add"));
				layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/add/y"));
				layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/normalized_contentFrames"));
				layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/normalized_contentFrames/y"));
				layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/sub"));
				layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalisation/sub/y"));
			}
			if (modelType.value == ModelType.RefBut32Channels)
			{
				layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalized_contentFrames"));
				layerList.RemoveAt(FindLayerIndexByName(layerList, "StyleNetwork/normalized_contentFrames/y"));
			}

			// Remove final model post processing, post process happen in tensor to texture instead
			int postAdd = FindLayerIndexByName(layerList, "StyleNetwork/clamp_0_255/add");
			layerList.RemoveRange(postAdd, 5);

			// Correct wrong output layer list
			_model.outputs = new List<string>() { layerList[postAdd - 1].name };

			_model.layers = layerList;
			Model.Input input = _model.inputs[1];
			input.shape[0] = 0;
			input.shape[1] = _rtHandle.rt.height;
			input.shape[2] = _rtHandle.rt.width;
			input.shape[3] = 3;
			_model.inputs = new List<Model.Input> { _model.inputs[1] };
			//Create worker and execute it once at target resolution to prime all memory allocation (however in editor resolution can still change at runtime)
			_worker = WorkerFactory.CreateWorker(WorkerFactory.ValidateType(workerType.value), _model, debugModelLoading.value);
			Dictionary<string, Tensor> temp = new Dictionary<string, Tensor>();
			var inputTensor = new Tensor(input.shape, input.name);
			temp.Add("frame", inputTensor);
			_worker.Execute(temp);
			inputTensor.Dispose();
		}

		private void PatchRuntimeWorkerWithStylePrediction()
		{
			Debug.Assert(_worker != null);

			int savedAlphaBetasIndex = 0;
			Debug.Log("Begain For");
			for (int i = 0; i < _layerNameToPatch.Count; ++i)
			{
				var tensors = _worker.PeekConstants(_layerNameToPatch[i]);
				int channels = _predictionAlphasBetasData[savedAlphaBetasIndex].Length;
				Debug.Assert(channels == tensors[0].length);
				// for (int j = 0; j < channels; j++)
				// tensors[0][j] = _predictionAlphasBetasData[savedAlphaBetasIndex][j];
				// for (int j = 0; j < channels; j++)
				{
					// tensors[1][j] = _predictionAlphasBetasData[savedAlphaBetasIndex + 1][j];
				}
				// Debug.Log("i = " + i);
				// var v = tensors[1][0];
				// Debug.Log("v = " + v);

				// Debug.Log(v);
				tensors[0].data.Upload(_predictionAlphasBetasData[savedAlphaBetasIndex], tensors[0].shape);
				tensors[1].data.Upload(_predictionAlphasBetasData[savedAlphaBetasIndex + 1], tensors[1].shape);

				// tensors[0].FlushCache(true);
				// tensors[1].FlushCache(true);
				savedAlphaBetasIndex += 2;

				// tensors[0].Dispose();
				// tensors[1].Dispose();
			}
			Debug.Log("End For");
		}

		private int FindLayerIndexByName(List<Layer> list, string name)
		{
			return list.FindIndex((layer => layer.name == name));
		}
	}
}