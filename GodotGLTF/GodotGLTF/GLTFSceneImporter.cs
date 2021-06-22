using GLTF;
using GLTF.Extensions;
using GLTF.Schema;
using GLTF.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using GodotGLTF.Cache;
using GodotGLTF.Extensions;
using GodotGLTF.Loader;
using Matrix4x4 = GLTF.Math.Matrix4x4;
using System.Diagnostics;
#if !WINDOWS_UWP
using ThreadPriority = System.Threading.ThreadPriority;
#endif

namespace GodotGLTF
{
	public class ImportOptions
	{
#pragma warning disable CS0618 // Type or member is obsolete
		public ILoader ExternalDataLoader = null;
#pragma warning restore CS0618 // Type or member is obsolete

		/// <summary>
		/// Optional <see cref="IDataLoader"/> for loading references from the GLTF to external streams.  May also optionally implement <see cref="IDataLoader2"/>.
		/// </summary>
		public IDataLoader DataLoader = null;
		public AsyncCoroutineHelper AsyncCoroutineHelper = null;
		public bool ThrowOnLowMemory = true;
	}

	public class UnityMeshData
	{
		public Vector3[][] Vertices;
		public Vector3[][] Normals;
		public float[][] Tangents;
		public Vector2[][] Uv1;
		public Vector2[][] Uv2;
		public Color[][] Colors;
		public float[][] BoneWeights;
		public float[][] Joints;

		public Vector3[,][] MorphTargetVertices;
		public Vector3[,][] MorphTargetNormals;
		public Vector3[,][] MorphTargetTangents;

		public Godot.Mesh.PrimitiveType[] Topology;
		public int[][] Indices;
	}

	public struct ImportProgress
	{
		public bool IsDownloaded;

		public int NodeTotal;
		public int NodeLoaded;

		public int TextureTotal;
		public int TextureLoaded;

		public int BuffersTotal;
		public int BuffersLoaded;

		public float Progress
		{
			get
			{
				int total = NodeTotal + TextureTotal + BuffersTotal;
				int loaded = NodeLoaded + TextureLoaded + BuffersLoaded;
				if (total > 0)
				{
					return (float)loaded / total;
				}
				else
				{
					return 0.0f;
				}
			}
		}
	}

	public struct ImportStatistics
	{
		public long TriangleCount;
		public long VertexCount;
	}

	/// <summary>
	/// Converts gltf animation data to unity
	/// </summary>
	public delegate void ValuesConvertion(NumericArray data, int frame, Godot.Collections.Dictionary value);

	public class GLTFSceneImporter : IDisposable
	{
		public enum ColliderType
		{
			None,
			Box,
			Mesh,
			MeshConvex
		}

		/// <summary>
		/// Maximum LOD
		/// </summary>
		public int MaximumLod = 300;

		/// <summary>
		/// Timeout for certain threading operations
		/// </summary>
		public int Timeout = 8;

		private bool _isMultithreaded;

		/// <summary>
		/// Use Multithreading or not.
		/// In editor, this is always false. This is to prevent a freeze in editor (noticed in Unity versions 2017.x and 2018.x)
		/// </summary>
		public bool IsMultithreaded
		{
			get
			{
				return Engine.EditorHint ? false : _isMultithreaded;
			}
			set
			{
				_isMultithreaded = value;
			}
		}

		/// <summary>
		/// The parent Node for the created Node
		/// </summary>
		public Godot.Node SceneParent { get; set; }

		/// <summary>
		/// The last created object
		/// </summary>
		public Godot.Node CreatedObject { get; private set; }

		/// <summary>
		/// Adds colliders to primitive objects when created
		/// </summary>
		public ColliderType Collider { get; set; }

		/// <summary>
		/// Override for the shader to use on created materials
		/// </summary>
		public string CustomShaderName { get; set; }

		/// <summary>
		/// Whether to keep a CPU-side copy of the mesh after upload to GPU (for example, in case normals/tangents need recalculation)
		/// </summary>
		public bool KeepCPUCopyOfMesh = true;

		/// <summary>
		/// Whether to keep a CPU-side copy of the texture after upload to GPU
		/// </summary>
		/// <remaks>
		/// This is is necessary when a texture is used with different sampler states, as Unity doesn't allow setting
		/// of filter and wrap modes separately form the texture object. Setting this to false will omit making a copy
		/// of a texture in that case and use the original texture's sampler state wherever it's referenced; this is
		/// appropriate in cases such as the filter and wrap modes being specified in the shader instead
		/// </remaks>
		public bool KeepCPUCopyOfTexture = true;

		/// <summary>
		/// Specifies whether the MipMap chain should be generated for model textures
		/// </summary>
		public bool GenerateMipMapsForTextures = true;

		/// <summary>
		/// When screen coverage is above threashold and no LOD mesh cull the object
		/// </summary>
		public bool CullFarLOD = false;

		/// <summary>
		/// Statistics from the scene
		/// </summary>
		public ImportStatistics Statistics;

		protected struct GLBStream
		{
			public Stream Stream;
			public long StartPosition;
		}

		protected ImportOptions _options;
		protected MemoryChecker _memoryChecker;

		protected Godot.Node _lastLoadedScene;
		protected readonly GLTFMaterial DefaultMaterial = new GLTFMaterial();
		protected MaterialCacheData _defaultLoadedMaterial = null;

		protected string _gltfFileName;
		protected GLBStream _gltfStream;
		protected GLTFRoot _gltfRoot;
		protected AssetCache _assetCache;
		protected bool _isRunning = false;

		protected ImportProgress progressStatus = default(ImportProgress);
		protected IProgress<ImportProgress> progress = null;

		public GLTFSceneImporter(string gltfFileName, ImportOptions options)
		{
			_gltfFileName = gltfFileName;
			_options = options;
			if (_options.DataLoader == null)
			{
				_options.DataLoader = LegacyLoaderWrapper.Wrap(_options.ExternalDataLoader);
			}
		}

		public GLTFSceneImporter(GLTFRoot rootNode, Stream gltfStream, ImportOptions options)
		{
			_gltfRoot = rootNode;

			if (gltfStream != null)
			{
				_gltfStream = new GLBStream { Stream = gltfStream, StartPosition = gltfStream.Position };
			}

			_options = options;
			if (_options.DataLoader == null)
			{
				_options.DataLoader = LegacyLoaderWrapper.Wrap(_options.ExternalDataLoader);
			}
		}

		/// <summary>
		/// Creates a GLTFSceneBuilder object which will be able to construct a scene based off a url
		/// </summary>
		/// <param name="gltfFileName">glTF file relative to data loader path</param>
		/// <param name="externalDataLoader">Loader to load external data references</param>
		/// <param name="asyncCoroutineHelper">Helper to load coroutines on a seperate thread</param>
		[Obsolete("Please switch to GLTFSceneImporter(string gltfFileName, ImportOptions options).  This constructor is deprecated and will be removed in a future release.")]
		public GLTFSceneImporter(string gltfFileName, ILoader externalDataLoader, AsyncCoroutineHelper asyncCoroutineHelper)
			: this(externalDataLoader, asyncCoroutineHelper)
		{
			_gltfFileName = gltfFileName;
		}

		[Obsolete("Please switch to GLTFSceneImporter(GLTFRoot rootNode, Stream gltfStream, ImportOptions options).  This constructor is deprecated and will be removed in a future release.")]
		public GLTFSceneImporter(GLTFRoot rootNode, ILoader externalDataLoader, AsyncCoroutineHelper asyncCoroutineHelper, Stream gltfStream = null)
			: this(externalDataLoader, asyncCoroutineHelper)
		{
			_gltfRoot = rootNode;

			if (gltfStream != null)
			{
				_gltfStream = new GLBStream { Stream = gltfStream, StartPosition = gltfStream.Position };
			}
		}

		[Obsolete("Only called by obsolete public constructors.  This will be removed when those obsolete constructors are removed.")]
		private GLTFSceneImporter(ILoader externalDataLoader, AsyncCoroutineHelper asyncCoroutineHelper)
		{
			_options = new ImportOptions
			{
				DataLoader = LegacyLoaderWrapper.Wrap(externalDataLoader),
				AsyncCoroutineHelper = asyncCoroutineHelper
			};
		}

		public void Dispose()
		{
			Cleanup();
		}

		public Godot.Node LastLoadedScene
		{
			get { return _lastLoadedScene; }
		}

		/// <summary>
		/// Loads a glTF Scene into the LastLoadedScene field
		/// </summary>
		/// <param name="sceneIndex">The scene to load, If the index isn't specified, we use the default index in the file. Failing that we load index 0.</param>
		/// <param name="showSceneObj"></param>
		/// <param name="onLoadComplete">Callback function for when load is completed</param>
		/// <param name="cancellationToken">Cancellation token for loading</param>
		/// <returns></returns>
		public async Task LoadSceneAsync(int sceneIndex = -1, bool showSceneObj = true, Action<Godot.Node, ExceptionDispatchInfo> onLoadComplete = null, CancellationToken cancellationToken = default(CancellationToken), IProgress<ImportProgress> progress = null)
		{
			try
			{
				lock (this)
				{
					if (_isRunning)
					{
						throw new GLTFLoadException("Cannot call LoadScene while GLTFSceneImporter is already running");
					}

					_isRunning = true;
				}

				if (_options.ThrowOnLowMemory)
				{
					_memoryChecker = new MemoryChecker();
				}

				this.progressStatus = new ImportProgress();
				this.progress = progress;

				Statistics = new ImportStatistics();
				progress?.Report(progressStatus);

				if (_gltfRoot == null)
				{
					await LoadJson(_gltfFileName);
					progressStatus.IsDownloaded = true;
				}

				cancellationToken.ThrowIfCancellationRequested();

				if (_assetCache == null)
				{
					_assetCache = new AssetCache(_gltfRoot);
				}

				await _LoadScene(sceneIndex, showSceneObj, cancellationToken);
			}
			catch (Exception ex)
			{
				Cleanup();

				onLoadComplete?.Invoke(null, ExceptionDispatchInfo.Capture(ex));
				throw;
			}
			finally
			{
				lock (this)
				{
					_isRunning = false;
				}
			}

			Debug.Assert(progressStatus.NodeLoaded == progressStatus.NodeTotal, $"Nodes loaded ({progressStatus.NodeLoaded}) does not match node total in the scene ({progressStatus.NodeTotal})");
			Debug.Assert(progressStatus.TextureLoaded <= progressStatus.TextureTotal, $"Textures loaded ({progressStatus.TextureLoaded}) is larger than texture total in the scene ({progressStatus.TextureTotal})");

			onLoadComplete?.Invoke(LastLoadedScene, null);
		}
		/*FIXME
		public IEnumerator LoadScene(int sceneIndex = -1, bool showSceneObj = true, Action<GameObject, ExceptionDispatchInfo> onLoadComplete = null)
		{
			return LoadSceneAsync(sceneIndex, showSceneObj, onLoadComplete).AsCoroutine();
		}

		/// <summary>
		/// Loads a node tree from a glTF file into the LastLoadedScene field
		/// </summary>
		/// <param name="nodeIndex">The node index to load from the glTF</param>
		/// <returns></returns>
		public async Task LoadNodeAsync(int nodeIndex, CancellationToken cancellationToken)
		{
			await SetupLoad(async () =>
			{
				CreatedObject = await GetNode(nodeIndex, cancellationToken);
				InitializeGltfTopLevelObject();
			});
		}
				*/
		/// <summary>
		/// Load a Material from the glTF by index
		/// </summary>
		/// <param name="materialIndex"></param>
		/// <returns></returns>
		public virtual async Task<Material> LoadMaterialAsync(int materialIndex)
		{
			await SetupLoad(async () =>
			{
				if (materialIndex < 0 || materialIndex >= _gltfRoot.Materials.Count)
				{
					throw new ArgumentException($"There is no material for index {materialIndex}");
				}

				if (_assetCache.MaterialCache[materialIndex] == null)
				{
					var def = _gltfRoot.Materials[materialIndex];
					await ConstructMaterialImageBuffers(def);
					await ConstructMaterial(def, materialIndex);
				}
			});
			return _assetCache.MaterialCache[materialIndex].UnityMaterialWithVertexColor;
		}

		/// <summary>
		/// Load a Mesh from the glTF by index
		/// </summary>
		/// <param name="meshIndex"></param>
		/// <returns></returns>
		public virtual async Task<ArrayMesh> LoadMeshAsync(int meshIndex, CancellationToken cancellationToken)
		{
			await SetupLoad(async () =>
			{
				if (meshIndex < 0 || meshIndex >= _gltfRoot.Meshes.Count)
				{
					throw new ArgumentException($"There is no mesh for index {meshIndex}");
				}

				if (_assetCache.MeshCache[meshIndex] == null)
				{
					var def = _gltfRoot.Meshes[meshIndex];
					await ConstructMeshAttributes(def, new MeshId() { Id = meshIndex, Root = _gltfRoot });
					await ConstructMesh(def, meshIndex, cancellationToken);
				}
			});
			return _assetCache.MeshCache[meshIndex].LoadedMesh;
		}

		/// <summary>
		/// Initializes the top-level created node by adding an instantiated GLTF object component to it,
		/// so that it can cleanup after itself properly when destroyed
		/// </summary>
		private void InitializeGltfTopLevelObject()
		{
			var instantiatedGltfObject = new InstantiatedGLTFObject();
			CreatedObject.AddChild(instantiatedGltfObject);
			instantiatedGltfObject.CachedData = new RefCountedCacheData
			(
				_assetCache.MaterialCache,
				_assetCache.MeshCache,
				_assetCache.TextureCache,
				_assetCache.ImageCache
			);
		}

		private async Task ConstructBufferData(GLTF.Schema.Node node, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();

			MeshId mesh = node.Mesh;
			if (mesh != null)
			{
				if (mesh.Value.Primitives != null)
				{
					await ConstructMeshAttributes(mesh.Value, mesh);
				}
			}

			if (node.Children != null)
			{
				foreach (NodeId child in node.Children)
				{
					await ConstructBufferData(child.Value, cancellationToken);
				}
			}

			const string msft_LODExtName = MSFT_LODExtensionFactory.EXTENSION_NAME;
			MSFT_LODExtension lodsextension = null;
			if (_gltfRoot.ExtensionsUsed != null
				&& _gltfRoot.ExtensionsUsed.Contains(msft_LODExtName)
				&& node.Extensions != null
				&& node.Extensions.ContainsKey(msft_LODExtName))
			{
				lodsextension = node.Extensions[msft_LODExtName] as MSFT_LODExtension;
				if (lodsextension != null && lodsextension.MeshIds.Count > 0)
				{
					for (int i = 0; i < lodsextension.MeshIds.Count; i++)
					{
						int lodNodeId = lodsextension.MeshIds[i];
						await ConstructBufferData(_gltfRoot.Nodes[lodNodeId], cancellationToken);
					}
				}
			}
		}
		private async Task ConstructMeshAttributes(GLTFMesh mesh, MeshId meshId)
		{
			int meshIndex = meshId.Id;

			if (_assetCache.MeshCache[meshIndex] == null)
				_assetCache.MeshCache[meshIndex] = new MeshCacheData();
			else if (_assetCache.MeshCache[meshIndex].Primitives.Count > 0)
				return;

			for (int i = 0; i < mesh.Primitives.Count; ++i)
			{
				MeshPrimitive primitive = mesh.Primitives[i];

				await ConstructPrimitiveAttributes(primitive, meshIndex, i);

				if (primitive.Material != null)
				{
					await ConstructMaterialImageBuffers(primitive.Material.Value);
				}
				if (primitive.Targets != null)
				{
					// read mesh primitive targets into assetcache
					await ConstructMeshTargets(primitive, meshIndex, i);
				}
			}
		}

		protected async Task ConstructImageBuffer(GLTFTexture texture, int textureIndex)
		{
			int sourceId = GetTextureSourceId(texture);
			if (_assetCache.ImageStreamCache[sourceId] == null)
			{
				GLTFImage image = _gltfRoot.Images[sourceId];

				// we only load the streams if not a base64 uri, meaning the data is in the uri
				if (image.Uri != null && !URIHelper.IsBase64Uri(image.Uri))
				{
					_assetCache.ImageStreamCache[sourceId] = await _options.DataLoader.LoadStreamAsync(image.Uri);
				}
				else if (image.Uri == null && image.BufferView != null && _assetCache.BufferCache[image.BufferView.Value.Buffer.Id] == null)
				{
					int bufferIndex = image.BufferView.Value.Buffer.Id;
					await ConstructBuffer(_gltfRoot.Buffers[bufferIndex], bufferIndex);
				}
			}

			if (_assetCache.TextureCache[textureIndex] == null)
			{
				_assetCache.TextureCache[textureIndex] = new TextureCacheData
				{
					TextureDefinition = texture
				};
			}
		}
#if false // FIXME
		protected IEnumerator WaitUntilEnum(WaitUntil waitUntil)
		{
			yield return waitUntil;
		}
#endif
		private async Task LoadJson(string jsonFilePath)
		{
#if !WINDOWS_UWP
			var dataLoader2 = _options.DataLoader as IDataLoader2;
			if (IsMultithreaded && dataLoader2 != null)
			{
				System.Threading.Thread loadThread = new System.Threading.Thread(() => _gltfStream.Stream = dataLoader2.LoadStream(jsonFilePath));
				loadThread.Priority = ThreadPriority.Highest;
				loadThread.Start();
				while (loadThread.IsAlive) ;
				//FIXME
				//RunCoroutineSync(WaitUntilEnum(new WaitUntil(() => !loadThread.IsAlive)));
			}
			else
#endif
			{
				_gltfStream.Stream = await _options.DataLoader.LoadStreamAsync(jsonFilePath);
			}

			_gltfStream.StartPosition = 0;

#if !WINDOWS_UWP
			if (IsMultithreaded)
			{
				System.Threading.Thread parseJsonThread = new System.Threading.Thread(() => GLTFParser.ParseJson(_gltfStream.Stream, out _gltfRoot, _gltfStream.StartPosition));
				parseJsonThread.Priority = ThreadPriority.Highest;
				parseJsonThread.Start();
				while (parseJsonThread.IsAlive) ;
				//FIXME
				//RunCoroutineSync(WaitUntilEnum(new WaitUntil(() => !parseJsonThread.IsAlive)));
				if (_gltfRoot == null)
				{
					throw new GLTFLoadException("Failed to parse glTF");
				}
			}
			else
#endif
			{
				GLTFParser.ParseJson(_gltfStream.Stream, out _gltfRoot, _gltfStream.StartPosition);
			}
		}

		private static void RunCoroutineSync(IEnumerator streamEnum)
		{
			var stack = new Stack<IEnumerator>();
			stack.Push(streamEnum);
			while (stack.Count > 0)
			{
				var enumerator = stack.Pop();
				if (enumerator.MoveNext())
				{
					stack.Push(enumerator);
					var subEnumerator = enumerator.Current as IEnumerator;
					if (subEnumerator != null)
					{
						stack.Push(subEnumerator);
					}
				}
			}
		}

		/// <summary>
		/// Creates a scene based off loaded JSON. Includes loading in binary and image data to construct the meshes required.
		/// </summary>
		/// <param name="sceneIndex">The bufferIndex of scene in gltf file to load</param>
		/// <returns></returns>
		protected async Task _LoadScene(int sceneIndex = -1, bool showSceneObj = true, CancellationToken cancellationToken = default(CancellationToken))
		{
			GLTFScene scene;

			if (sceneIndex >= 0 && sceneIndex < _gltfRoot.Scenes.Count)
			{
				scene = _gltfRoot.Scenes[sceneIndex];
			}
			else
			{
				scene = _gltfRoot.GetDefaultScene();
			}

			if (scene == null)
			{
				throw new GLTFLoadException("No default scene in gltf file.");
			}

			GetGtlfContentTotals(scene);

			await ConstructScene(scene, showSceneObj, cancellationToken);

			if (SceneParent != null)
			{
				SceneParent.AddChild(CreatedObject);
			}

			_lastLoadedScene = CreatedObject;
		}

		private void GetGtlfContentTotals(GLTFScene scene)
		{
			// Count Nodes
			Queue<NodeId> nodeQueue = new Queue<NodeId>();

			// Add scene nodes
			if (scene.Nodes != null)
			{
				for (int i = 0; i < scene.Nodes.Count; ++i)
				{
					nodeQueue.Enqueue(scene.Nodes[i]);
				}
			}

			// BFS of nodes
			while (nodeQueue.Count > 0)
			{
				var cur = nodeQueue.Dequeue();
				progressStatus.NodeTotal++;

				if (cur.Value.Children != null)
				{
					for (int i = 0; i < cur.Value.Children.Count; ++i)
					{
						nodeQueue.Enqueue(cur.Value.Children[i]);
					}
				}
			}

			// Total textures
			progressStatus.TextureTotal += _gltfRoot.Textures?.Count ?? 0;

			// Total buffers
			progressStatus.BuffersTotal += _gltfRoot.Buffers?.Count ?? 0;

			// Send report
			progress?.Report(progressStatus);
		}

		private async Task<BufferCacheData> GetBufferData(BufferId bufferId)
		{
			if (_assetCache.BufferCache[bufferId.Id] == null)
			{
				await ConstructBuffer(bufferId.Value, bufferId.Id);
			}

			return _assetCache.BufferCache[bufferId.Id];
		}

		protected async Task ConstructBuffer(GLTFBuffer buffer, int bufferIndex)
		{
			if (buffer.Uri == null)
			{
				Debug.Assert(_assetCache.BufferCache[bufferIndex] == null);
				_assetCache.BufferCache[bufferIndex] = ConstructBufferFromGLB(bufferIndex);

				progressStatus.BuffersLoaded++;
				progress?.Report(progressStatus);
			}
			else
			{
				Stream bufferDataStream = null;
				var uri = buffer.Uri;

				byte[] bufferData;
				URIHelper.TryParseBase64(uri, out bufferData);
				if (bufferData != null)
				{
					bufferDataStream = new MemoryStream(bufferData, 0, bufferData.Length, false, true);
				}
				else
				{
					bufferDataStream = await _options.DataLoader.LoadStreamAsync(buffer.Uri);
				}

				Debug.Assert(_assetCache.BufferCache[bufferIndex] == null);
				_assetCache.BufferCache[bufferIndex] = new BufferCacheData
				{
					Stream = bufferDataStream
				};

				progressStatus.BuffersLoaded++;
				progress?.Report(progressStatus);
			}
		}

		protected async Task ConstructImage(GLTFImage image, int imageCacheIndex, bool markGpuOnly, bool isLinear)
		{
			if (_assetCache.ImageCache[imageCacheIndex] == null)
			{
				Stream stream = null;
				if (image.Uri == null)
				{
					var bufferView = image.BufferView.Value;
					var data = new byte[bufferView.ByteLength];

					BufferCacheData bufferContents = _assetCache.BufferCache[bufferView.Buffer.Id];
					bufferContents.Stream.Position = bufferView.ByteOffset + bufferContents.ChunkOffset;
					stream = new SubStream(bufferContents.Stream, 0, data.Length);
				}
				else
				{
					string uri = image.Uri;

					byte[] bufferData;
					URIHelper.TryParseBase64(uri, out bufferData);
					if (bufferData != null)
					{
						stream = new MemoryStream(bufferData, 0, bufferData.Length, false, true);
					}
					else
					{
						stream = _assetCache.ImageStreamCache[imageCacheIndex];
					}
				}

				await YieldOnTimeoutAndThrowOnLowMemory();
				await ConstructUnityTexture(stream, markGpuOnly, isLinear, image, imageCacheIndex);
			}
		}

		protected virtual async Task ConstructUnityTexture(Stream stream, bool markGpuOnly, bool isLinear, GLTFImage image, int imageCacheIndex)
		{
			Image img = new Image();
			ImageTexture imageTexture = new ImageTexture();
			imageTexture.ResourceName = nameof(GLTFSceneImporter) + (image.Name != null ? ("." + image.Name) : "");

			if (stream is MemoryStream)
			{
				using (MemoryStream memoryStream = stream as MemoryStream)
				{

					await YieldOnTimeoutAndThrowOnLowMemory();
					if (LoadImageBuffer(img, memoryStream.ToArray(), image.MimeType) != Error.Ok)
						throw new NotSupportedException($"glTF: Couldn't load image index '{imageCacheIndex}' with its given mimetype: {image.MimeType}.");
				}
			}
			else
			{
				byte[] buffer = new byte[stream.Length];

				// todo: potential optimization is to split stream read into multiple frames (or put it on a thread?)
				if (stream.Length > int.MaxValue)
				{
					throw new Exception("Stream is larger than can be copied into byte array");
				}
				stream.Read(buffer, 0, (int)stream.Length);

				await YieldOnTimeoutAndThrowOnLowMemory();
				if (LoadImageBuffer(img, buffer, image.MimeType) != Error.Ok)
					throw new NotSupportedException($"glTF: Couldn't load image index '{imageCacheIndex}' with its given mimetype: {image.MimeType}.");
			}

			Debug.Assert(_assetCache.ImageCache[imageCacheIndex] == null, "ImageCache should not be loaded multiple times");
			progressStatus.TextureLoaded++;
			progress?.Report(progressStatus);
			imageTexture.CreateFromImage(img);
			_assetCache.ImageCache[imageCacheIndex] = imageTexture;
		}

		private Error LoadImageBuffer(Image img, byte[] buffer, string mimeType)
		{
			Error error;

			switch (mimeType)
			{
				case "image/png":
					error = img.LoadPngFromBuffer(buffer);
					break;
				case "image/jpeg":
					error = img.LoadJpgFromBuffer(buffer);
					break;
				default:
					// We can land here if we got an URI with base64-encoded data with application/* MIME type,
					// and the optional mimeType property was not defined to tell us how to handle this data (or was invalid).
					// So let's try PNG first, then JPEG.
					error = img.LoadPngFromBuffer(buffer);
					if (error != Error.Ok)
						error = img.LoadJpgFromBuffer(buffer);
					break;
			}
			return error;
		}

		protected virtual async Task ConstructMeshTargets(MeshPrimitive primitive, int meshIndex, int primitiveIndex)
		{
			var newTargets = new List<Dictionary<string, AttributeAccessor>>(primitive.Targets.Count);
			_assetCache.MeshCache[meshIndex].Primitives[primitiveIndex].Targets = newTargets;

			for (int i = 0; i < primitive.Targets.Count; i++)
			{
				var target = primitive.Targets[i];
				newTargets.Add(new Dictionary<string, AttributeAccessor>());

				//NORMALS, POSITIONS, TANGENTS
				foreach (var targetAttribute in target)
				{
					BufferId bufferIdPair = targetAttribute.Value.Value.BufferView.Value.Buffer;
					GLTFBuffer buffer = bufferIdPair.Value;
					int bufferID = bufferIdPair.Id;

					if (_assetCache.BufferCache[bufferID] == null)
					{
						await ConstructBuffer(buffer, bufferID);
					}

					newTargets[i][targetAttribute.Key] = new AttributeAccessor
					{
						AccessorId = targetAttribute.Value,
						Stream = _assetCache.BufferCache[bufferID].Stream,
						Offset = (uint)_assetCache.BufferCache[bufferID].ChunkOffset
					};

				}

				var att = newTargets[i];
				GLTFHelpers.BuildTargetAttributes(ref att);
			}
		}

		protected virtual async Task ConstructPrimitiveAttributes(MeshPrimitive primitive, int meshIndex, int primitiveIndex)
		{
			var primData = new MeshCacheData.PrimitiveCacheData();
			_assetCache.MeshCache[meshIndex].Primitives.Add(primData);

			var attributeAccessors = primData.Attributes;
			foreach (var attributePair in primitive.Attributes)
			{
				var bufferId = attributePair.Value.Value.BufferView.Value.Buffer;
				var bufferData = await GetBufferData(bufferId);

				attributeAccessors[attributePair.Key] = new AttributeAccessor
				{
					AccessorId = attributePair.Value,
					Stream = bufferData.Stream,
					Offset = (uint)bufferData.ChunkOffset
				};
			}

			if (primitive.Indices != null)
			{
				var bufferId = primitive.Indices.Value.BufferView.Value.Buffer;
				var bufferData = await GetBufferData(bufferId);

				attributeAccessors[SemanticProperties.INDICES] = new AttributeAccessor
				{
					AccessorId = primitive.Indices,
					Stream = bufferData.Stream,
					Offset = (uint)bufferData.ChunkOffset
				};
			}
			try
			{
				GLTFHelpers.BuildMeshAttributes(ref attributeAccessors);
			}
			catch (GLTFLoadException e)
			{
				GD.Print(e.ToString());
			}
			//TransformAttributes(ref attributeAccessors);
		}

		protected void TransformAttributes(ref Dictionary<string, AttributeAccessor> attributeAccessors)
		{
			foreach (var name in attributeAccessors.Keys)
			{
				var aa = attributeAccessors[name];
				switch (name)
				{
					case SemanticProperties.POSITION:
					case SemanticProperties.NORMAL:
						SchemaExtensions.ConvertVector3CoordinateSpace(ref aa, SchemaExtensions.CoordinateSpaceConversionScale);
						break;
					case SemanticProperties.TANGENT:
						SchemaExtensions.ConvertVector4CoordinateSpace(ref aa, SchemaExtensions.TangentSpaceConversionScale);
						break;
					case SemanticProperties.TEXCOORD_0:
					case SemanticProperties.TEXCOORD_1:
					case SemanticProperties.TEXCOORD_2:
					case SemanticProperties.TEXCOORD_3:
						SchemaExtensions.FlipTexCoordArrayV(ref aa);
						break;
				}
			}
		}

		#region Animation
		static string RelativePathFrom(Godot.Node self, Godot.Node root)
		{
			var path = new List<String>();
			for (var current = self; current != null; current = current.GetParent())
			{
				if (current == root)
				{
					return String.Join("/", path.ToArray());
				}

				path.Insert(0, current.Name);
			}

			throw new Exception("no RelativePath");
		}

		protected virtual async Task BuildAnimationSamplers(GLTFAnimation animation, int animationId)
		{
			// look up expected data types
			var typeMap = new Dictionary<int, string>();
			foreach (var channel in animation.Channels)
			{
				typeMap[channel.Sampler.Id] = channel.Target.Path.ToString();
			}

			var samplers = _assetCache.AnimationCache[animationId].Samplers;
			var samplersByType = new Dictionary<string, List<AttributeAccessor>>
			{
				{"time", new List<AttributeAccessor>(animation.Samplers.Count)}
			};

			for (var i = 0; i < animation.Samplers.Count; i++)
			{
				// no sense generating unused samplers
				if (!typeMap.ContainsKey(i))
				{
					continue;
				}

				var samplerDef = animation.Samplers[i];

				samplers[i].Interpolation = samplerDef.Interpolation;

				// set up input accessors
				BufferCacheData inputBufferCacheData = await GetBufferData(samplerDef.Input.Value.BufferView.Value.Buffer);
				AttributeAccessor attributeAccessor = new AttributeAccessor
				{
					AccessorId = samplerDef.Input,
					Stream = inputBufferCacheData.Stream,
					Offset = inputBufferCacheData.ChunkOffset
				};

				samplers[i].Input = attributeAccessor;
				samplersByType["time"].Add(attributeAccessor);

				// set up output accessors
				BufferCacheData outputBufferCacheData = await GetBufferData(samplerDef.Output.Value.BufferView.Value.Buffer);
				attributeAccessor = new AttributeAccessor
				{
					AccessorId = samplerDef.Output,
					Stream = outputBufferCacheData.Stream,
					Offset = outputBufferCacheData.ChunkOffset
				};

				samplers[i].Output = attributeAccessor;

				if (!samplersByType.ContainsKey(typeMap[i]))
				{
					samplersByType[typeMap[i]] = new List<AttributeAccessor>();
				}

				samplersByType[typeMap[i]].Add(attributeAccessor);
			}

			// populate attributeAccessors with buffer data
			GLTFHelpers.BuildAnimationSamplers(ref samplersByType);
		}

		protected void SetAnimationCurve(
			Animation clip,
			int track,
			NumericArray input,
			NumericArray output,
			InterpolationType mode,
			Spatial node,
			ValuesConvertion getConvertedValues)
		{
			var frameCount = input.AsFloats.Length;

			// copy all the key frame data to cache
			for (var i = 0; i < frameCount; ++i)
			{
				var time = input.AsFloats[i];
				if (clip.Length < time)
					clip.Length = time;

				Godot.Collections.Dictionary value = null;
				var keyIdx = clip.TrackFindKey(track, time, true);
				if (keyIdx != -1)
				{
					value = clip.TrackGetKeyValue(track, keyIdx) as Godot.Collections.Dictionary;
					clip.TrackRemoveKey(track, keyIdx);
				}
				if (value == null)
				{
					value = new Godot.Collections.Dictionary();
					value["scale"] = node.Scale;
					value["rotation"] = node.Transform.basis.Quat();
					value["location"] = node.Transform.origin;
				}

				float[] inTangents = null;
				float[] outTangents = null;
				if (mode == InterpolationType.CUBICSPLINE)
				{
					// For cubic spline, the output will contain 3 values per keyframe; inTangent, dataPoint, and outTangent.
					// https://github.com/KhronosGroup/glTF/blob/master/specification/2.0/README.md#appendix-c-spline-interpolation

					var cubicIndex = i * 3;
					//FIXME: CUBICSPLINE is not supported.
					//inTangents = getConvertedValues(output, cubicIndex);
					getConvertedValues(output, cubicIndex + 1, value);
					//FIXME: CUBICSPLINE is not supported.
					//outTangents = getConvertedValues(output, cubicIndex + 2);
				}
				else
				{
					// For other interpolation types, the output will only contain one value per keyframe
					getConvertedValues(output, i, value);
				}

				clip.TrackInsertKey(track, time, value);
			}
		}

		protected async Task<Animation> ConstructClip(Spatial root, int animationId, CancellationToken cancellationToken)
		{
			GLTFAnimation animation = _gltfRoot.Animations[animationId];

			AnimationCacheData animationCache = _assetCache.AnimationCache[animationId];
			if (animationCache == null)
			{
				animationCache = new AnimationCacheData(animation.Samplers.Count);
				_assetCache.AnimationCache[animationId] = animationCache;
			}
			else if (animationCache.LoadedAnimationClip != null)
			{
				return animationCache.LoadedAnimationClip;
			}

			// unpack accessors
			await BuildAnimationSamplers(animation, animationId);

			// init clip
			Animation clip = new Animation
			{
				ResourceName = animation.Name ?? string.Format("animation{0}", animationId),
				Length = 0
			};
			_assetCache.AnimationCache[animationId].LoadedAnimationClip = clip;

			// needed because Animator component is unavailable at runtime
			//FIXME
			//clip.legacy = true;

			foreach (AnimationChannel channel in animation.Channels)
			{
				AnimationSamplerCacheData samplerCache = animationCache.Samplers[channel.Sampler.Id];
				if (channel.Target.Node == null)
				{
					// If a channel doesn't have a target node, then just skip it.
					// This is legal and is present in one of the asset generator models, but means that animation doesn't actually do anything.
					// https://github.com/KhronosGroup/glTF-Asset-Generator/tree/master/Output/Positive/Animation_NodeMisc
					// Model 08
					continue;
				}
				Spatial node = await GetNode(channel.Target.Node.Id, cancellationToken) as Spatial;
				string propertyName;
				string relativePath;
				if (node.IsInGroup("bones"))
				{
					var skeleton = node.GetMeta("skeleton") as Skeleton;
					relativePath = RelativePathFrom(skeleton, root);
					propertyName = node.Name;
				}
				else
				{
					relativePath = RelativePathFrom(node, root);
					propertyName = "";
				}

				NumericArray input = samplerCache.Input.AccessorContent,
					output = samplerCache.Output.AccessorContent;
				var track = clip.FindTrack($"{relativePath}:{propertyName}");
				if (track == -1)
				{
					track = clip.AddTrack(Animation.TrackType.Transform);
					clip.TrackSetPath(track, $"{relativePath}:{propertyName}");
					clip.TrackSetInterpolationLoopWrap(track, false);
				}

				switch (samplerCache.Interpolation)
				{
					case InterpolationType.CATMULLROMSPLINE:
						clip.TrackSetInterpolationType(track, Animation.InterpolationType.Cubic);
						break;
					case InterpolationType.LINEAR:
						clip.TrackSetInterpolationType(track, Animation.InterpolationType.Linear);
						break;
					case InterpolationType.STEP:
						clip.TrackSetInterpolationType(track, Animation.InterpolationType.Nearest);
						break;
					case InterpolationType.CUBICSPLINE:
						//FIXME: CUBICSPLINE is not supported.
						break;

					default:
						throw new NotImplementedException();
				}

				switch (channel.Target.Path)
				{
					case GLTFAnimationChannelPath.translation:
						SetAnimationCurve(clip, track, input, output,
										  samplerCache.Interpolation, node,
										  (data, frame, value) =>
										  {
											  var position = data.AsVec3s[frame].ToUnityVector3Convert();
											  value["location"] = position;
										  });
						break;

					case GLTFAnimationChannelPath.rotation:
						SetAnimationCurve(clip, track, input, output,
										  samplerCache.Interpolation, node,
										  (data, frame, value) =>
										  {
											  var rotation = data.AsVec4s[frame];
											  var quaternion = new GLTF.Math.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W).ToUnityQuaternionConvert();
											  value["rotation"] = quaternion.Normalized();
										  });

						break;

					case GLTFAnimationChannelPath.scale:
						SetAnimationCurve(clip, track, input, output,
										  samplerCache.Interpolation, node,
										  (data, frame, value) =>
										  {
											  var scale = data.AsVec3s[frame].ToUnityVector3Raw();
											  value["scale"] = scale;
										  });
						break;

					case GLTFAnimationChannelPath.weights:
						var primitives = channel.Target.Node.Value.Mesh.Value.Primitives;
						var targetCount = primitives[0].Targets.Count;
						propertyName = "weights";

						var time = input.AsFloats;
						var data = output.AsFloats;
						if (clip.Length < time.Max())
							clip.Length = time.Max();

						for (int targetIndex = 0; targetIndex < targetCount; targetIndex++)
						{
							var targetName = primitives[0].TargetNames != null ? primitives[0].TargetNames[targetIndex] : $"Morphtarget{targetIndex}";
							int keyframe = clip.AddTrack(Animation.TrackType.Value);
							clip.TrackSetPath(keyframe, $"{relativePath}/MeshInstance:blend_shapes/{targetName}");
							clip.TrackSetInterpolationLoopWrap(keyframe, false);

							for (int timeIndex = 0, dataIndex = targetIndex; timeIndex < time.Length; dataIndex += targetCount, timeIndex++)
							{
								clip.TrackInsertKey(keyframe, time[timeIndex], data[dataIndex]);
							}

							switch (samplerCache.Interpolation)
							{
								case InterpolationType.CATMULLROMSPLINE:
									clip.TrackSetInterpolationType(keyframe, Animation.InterpolationType.Cubic);
									break;
								case InterpolationType.LINEAR:
									clip.TrackSetInterpolationType(keyframe, Animation.InterpolationType.Linear);
									break;
								case InterpolationType.STEP:
									clip.TrackSetInterpolationType(keyframe, Animation.InterpolationType.Nearest);
									break;
								case InterpolationType.CUBICSPLINE:
									//FIXME: CUBICSPLINE is not supported.
									break;

								default:
									throw new NotImplementedException();
							}
						}

						break;

					default:
						GD.PushWarning("Cannot read GLTF animation path");
						break;
				} // switch target type
			} // foreach channel

			var trackEnd = clip.GetTrackCount();
			for (int track = 0; track < trackEnd; track++)
			{
				if (clip.TrackGetType(track) != Animation.TrackType.Transform
					|| !clip.TrackGetPath(track).ToString().Contains("Skeleton"))
					continue;
				var skeleton = root.GetNode<Skeleton>(clip.TrackGetPath(track));
				if (skeleton == null)
					continue;

				var keyEnd = clip.TrackGetKeyCount(track);
				var boneName = clip.TrackGetPath(track).GetSubname(0);
				for (int key = 0; key < keyEnd; key++)
				{
					var trasformDictionary = clip.TrackGetKeyValue(track, key) as Godot.Collections.Dictionary;
					Basis basis = new Basis((Quat)trasformDictionary["rotation"]);
					basis.Scale = (Vector3)trasformDictionary["scale"];
					Transform xform = new Transform(basis, (Vector3)trasformDictionary["location"]);

					var bone = skeleton.FindBone(boneName);
					xform = skeleton.GetBoneRest(bone).AffineInverse() * xform;
					var time = clip.TrackGetKeyTime(track, key);
					clip.TrackRemoveKey(track, key);
					clip.TransformTrackInsertKey(track, time, xform.origin, xform.basis.RotationQuat().Normalized(), xform.basis.Scale);
				}
			}

			return clip;
		}
		#endregion

		protected virtual async Task ConstructScene(GLTFScene scene, bool showSceneObj, CancellationToken cancellationToken)
		{
			var sceneObj = new Godot.Spatial() { Name = string.IsNullOrEmpty(scene.Name) ? ("GLTFScene") : scene.Name };

			try
			{
				sceneObj.SetProcess(showSceneObj);

				for (int i = 0; i < scene.Nodes.Count; ++i)
				{
					NodeId node = scene.Nodes[i];
					Godot.Node nodeObj = await GetNode(node.Id, cancellationToken);
					if (nodeObj.IsInGroup("bones"))
						continue;

					sceneObj.AddChild(nodeObj);
					nodeObj.Owner = sceneObj;
				}
				if (_gltfRoot.Animations != null && _gltfRoot.Animations.Count > 0)
				{
					// create the AnimationClip that will contain animation data
					for (int i = 0; i < _gltfRoot.Animations.Count; ++i)
					{
						AnimationPlayer animationPlayer = new AnimationPlayer();
						sceneObj.AddChild(animationPlayer);
						animationPlayer.Owner = sceneObj;

						Animation clip = await ConstructClip(sceneObj, i, cancellationToken);

						clip.Loop = true;

						animationPlayer.Name = clip.ResourceName;
						animationPlayer.AddAnimation(clip.ResourceName, clip);
						animationPlayer.AssignedAnimation = clip.ResourceName;
						if (i == 0)
							animationPlayer.Play();
					}

				}

				CreatedObject = sceneObj;
				InitializeGltfTopLevelObject();
			}
			catch (Exception ex)
			{
				// If some failure occured during loading, clean up the scene
				sceneObj.Free();
				CreatedObject = null;

				if (ex is OutOfMemoryException)
				{
					//FIXME
					//Resources.UnloadUnusedAssets();
				}

				throw;
			}
		}

		private async Task<Godot.Node> GetNode(int nodeId, CancellationToken cancellationToken)
		{
			try
			{
				if (_assetCache.NodeCache[nodeId] == null)
				{
					if (nodeId >= _gltfRoot.Nodes.Count)
					{
						throw new ArgumentException("nodeIndex is out of range");
					}

					var node = _gltfRoot.Nodes[nodeId];

					cancellationToken.ThrowIfCancellationRequested();
					if (!IsMultithreaded)
					{
						await ConstructBufferData(node, cancellationToken);
					}
					else
					{
						await Task.Run(() => ConstructBufferData(node, cancellationToken));
					}

					await ConstructNode(node, nodeId, cancellationToken);
				}

				return _assetCache.NodeCache[nodeId];
			}
			catch (Exception ex)
			{
				// If some failure occured during loading, remove the node

				if (_assetCache.NodeCache[nodeId] != null)
				{
					_assetCache.NodeCache[nodeId].Free();
					_assetCache.NodeCache[nodeId] = null;
				}

				if (ex is OutOfMemoryException)
				{
					//FIXME
					//Resources.UnloadUnusedAssets();
				}

				throw;
			}
		}


		protected virtual async Task ConstructNode(GLTF.Schema.Node node, int nodeIndex, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (_assetCache.NodeCache[nodeIndex] != null)
			{
				return;
			}

			var nodeObj = new Spatial() { Name = string.IsNullOrEmpty(node.Name) ? ("GLTFNode" + nodeIndex) : node.Name };
			// If we're creating a really large node, we need it to not be visible in partial stages. So we hide it while we create it
			nodeObj.SetProcess(false);

			Vector3 position;
			Quat rotation;
			Vector3 scale;
			node.GetUnityTRSProperties(out position, out rotation, out scale);
			
			var basis = new Basis(rotation);
			basis.Scale = scale;
			nodeObj.Transform = new Transform(basis, position);
			_assetCache.NodeCache[nodeIndex] = nodeObj;

			if (node.Children != null)
			{
				foreach (var child in node.Children)
				{
					Godot.Node childObj = await GetNode(child.Id, cancellationToken);
					if (childObj.GetParent() == null)
						nodeObj.AddChild(childObj);
				}
			}
			
			/*FIXME: lod not support in godot3?
			const string msft_LODExtName = MSFT_LODExtensionFactory.EXTENSION_NAME;
			MSFT_LODExtension lodsextension = null;
			if (_gltfRoot.ExtensionsUsed != null
				&& _gltfRoot.ExtensionsUsed.Contains(msft_LODExtName)
				&& node.Extensions != null
				&& node.Extensions.ContainsKey(msft_LODExtName))
			{
				lodsextension = node.Extensions[msft_LODExtName] as MSFT_LODExtension;
				if (lodsextension != null && lodsextension.MeshIds.Count > 0)
				{
					int lodCount = lodsextension.MeshIds.Count + 1;
					if (!CullFarLOD)
					{
						//create a final lod with the mesh as the last LOD in file
						lodCount += 1;
					}
					LOD[] lods = new LOD[lodsextension.MeshIds.Count + 2];
					List<double> lodCoverage = lodsextension.GetLODCoverage(node);

					var lodGroupNodeObj = new GameObject(string.IsNullOrEmpty(node.Name) ? ("GLTFNode_LODGroup" + nodeIndex) : node.Name);
					lodGroupNodeObj.SetActive(false);
					nodeObj.transform.SetParent(lodGroupNodeObj.transform, false);
					MeshRenderer[] childRenders = nodeObj.GetComponentsInChildren<MeshRenderer>();
					lods[0] = new LOD(GetLodCoverage(lodCoverage, 0), childRenders);

					LODGroup lodGroup = lodGroupNodeObj.AddComponent<LODGroup>();
					for (int i = 0; i < lodsextension.MeshIds.Count; i++)
					{
						int lodNodeId = lodsextension.MeshIds[i];
						var lodNodeObj = await GetNode(lodNodeId, cancellationToken);
						lodNodeObj.transform.SetParent(lodGroupNodeObj.transform, false);
						childRenders = lodNodeObj.GetComponentsInChildren<MeshRenderer>();
						int lodIndex = i + 1;
						lods[lodIndex] = new LOD(GetLodCoverage(lodCoverage, lodIndex), childRenders);
					}

					if (!CullFarLOD)
					{
						//use the last mesh as the LOD
						lods[lodsextension.MeshIds.Count + 1] = new LOD(0, childRenders);
					}

					lodGroup.SetLODs(lods);
					lodGroup.RecalculateBounds();
					lodGroupNodeObj.SetActive(true);
					_assetCache.NodeCache[nodeIndex] = lodGroupNodeObj;
				}
			}
			*/

			if (node.Mesh != null)
			{
				var mesh = node.Mesh.Value;
				await ConstructMesh(mesh, node.Mesh.Id, cancellationToken);
				var arrayMesh = _assetCache.MeshCache[node.Mesh.Id].LoadedMesh;

				var materials = node.Mesh.Value.Primitives.Select(p =>
					p.Material != null ?
					_assetCache.MaterialCache[p.Material.Id].UnityMaterialWithVertexColor :
					_defaultLoadedMaterial.UnityMaterialWithVertexColor
				).ToArray();

				var meshInstance = new MeshInstance() { Name = "MeshInstance" };
				meshInstance.Mesh = arrayMesh;
				for (int i = 0; i < materials.Length; i++)
				{
					arrayMesh.SurfaceSetMaterial(i, (Material)materials[i].Duplicate());
				}

				var morphTargets = mesh.Primitives[0].Targets;
				var weights = node.Weights ?? mesh.Weights ??
					(morphTargets != null ? new List<double>(morphTargets.Select(mt => 0.0)) : null);
				if (node.Skin != null || weights != null)
				{
					if (node.Skin != null)
						await SetupBones(node.Skin.Value, nodeObj, meshInstance, cancellationToken);
					else
						nodeObj.AddChild(meshInstance);

					// morph target weights
					if (weights != null)
					{
						for (int i = 0; i < weights.Count; i++)
						{
							meshInstance.Set("blend_shapes/" + arrayMesh.GetBlendShapeName(i), weights[i]);
						}
					}
				}
				else
				{
					nodeObj.AddChild(meshInstance);
				}

				CollisionObject collisionObject = null;
				var collisionShape = new CollisionShape() {
					Name = "CollisionShape",
				};

				switch (Collider)
				{
					case ColliderType.Box:
						var aabb = arrayMesh.GetAabb();
						collisionObject = new Area() { 
							Name = "Area",
						};
						var boxShape = new BoxShape() {
							Extents = aabb.Size / 2,
						};
						collisionShape.Shape = boxShape;
						break;
					case ColliderType.Mesh:
						collisionObject = new Area() { Name = "Area" };
						var concavePolygonShape = new ConcavePolygonShape();
						concavePolygonShape.Data = arrayMesh.GetFaces();
						collisionShape.Shape = concavePolygonShape;
						break;
					case ColliderType.MeshConvex:
						collisionObject = new StaticBody() { Name = "StaticBody" };
						concavePolygonShape = new ConcavePolygonShape();
						concavePolygonShape.Data = arrayMesh.GetFaces();
						collisionShape.Shape = concavePolygonShape;
						break;
				}
				if (collisionObject != null)
				{
					collisionObject.AddChild(collisionShape);
					nodeObj.AddChild(collisionObject);
				}
			}
			/* TODO: implement camera (probably a flag to disable for VR as well)
			if (camera != null)
			{
				GameObject cameraObj = camera.Value.Create();
				cameraObj.transform.parent = nodeObj.transform;
			}
			*/

			nodeObj.SetProcess(true);

			progressStatus.NodeLoaded++;
			progress?.Report(progressStatus);
		}
#if false //FIXME
		float GetLodCoverage(List<double> lodcoverageExtras, int lodIndex)
		{
			if (lodcoverageExtras != null && lodIndex < lodcoverageExtras.Count)
			{
				return (float)lodcoverageExtras[lodIndex];
			}
			else
			{
				return 1.0f / (lodIndex + 2);
			}
		}
#endif
		protected virtual async Task SetupBones(GLTF.Schema.Skin skin, Spatial nodeObj, MeshInstance meshInstance, CancellationToken cancellationToken)
		{
			var boneCount = skin.Joints.Count;
			Godot.Node[] bones = new Godot.Node[boneCount];
			var godotSkin = new Godot.Skin() { ResourceName = "Skin" };
			var skeleton = new Skeleton() { Name = "Skeleton" };

			// TODO: build bindpose arrays only once per skin, instead of once per node
			Matrix4x4[] gltfBindPoses = null;
			if (skin.InverseBindMatrices != null)
			{
				var bufferId = skin.InverseBindMatrices.Value.BufferView.Value.Buffer;
				var bufferData = await GetBufferData(bufferId);
				AttributeAccessor attributeAccessor = new AttributeAccessor
				{
					AccessorId = skin.InverseBindMatrices,
					Stream = _assetCache.BufferCache[bufferId.Id].Stream,
					Offset = _assetCache.BufferCache[bufferId.Id].ChunkOffset
				};

				GLTFHelpers.BuildBindPoseSamplers(ref attributeAccessor);
				gltfBindPoses = attributeAccessor.AccessorContent.AsMatrix4x4s;
			}

			Transform[] bindPoses = new Transform[boneCount];
			for (int i = 0; i < boneCount; i++)
			{
				Spatial node = await GetNode(skin.Joints[i].Id, cancellationToken) as Spatial;
				node.AddToGroup("bones");
				node.SetMeta("skeleton", skeleton);
				skeleton.AddBone(node.Name);
				skeleton.SetBoneRest(i, node.Transform);

				var parent = node.GetParent();
				if (parent != null && parent != nodeObj)
				{
					skeleton.SetBoneParent(i, skeleton.FindBone(parent.Name));
					parent.RemoveChild(node);
				}

				bones[i] = node;
				bindPoses[i] = gltfBindPoses != null ? gltfBindPoses[i].ToGodotTransformConvert() : Transform.Identity;

				godotSkin.AddBind(i, bindPoses[i]);
				godotSkin.SetBindName(i, node.Name);
			}

			Godot.Node rootBoneNode = null;
			if (skin.Skeleton != null)
			{
				rootBoneNode = await GetNode(skin.Skeleton.Id, cancellationToken);
			}
			else
			{
				var rootBoneId = GLTFHelpers.FindCommonAncestor(skin.Joints);
				if (rootBoneId != null)
				{
					rootBoneNode = await GetNode(rootBoneId.Id, cancellationToken);
				}
				else
				{
					throw new Exception("glTF skin joints do not share a root node!");
				}
			}
			if (bones.Contains(rootBoneNode))
				rootBoneNode = rootBoneNode.GetParent();

			rootBoneNode?.AddChild(nodeObj);
			nodeObj.AddChild(skeleton);
			skeleton.AddChild(meshInstance);
			meshInstance.Owner = skeleton;
			meshInstance.Skin = godotSkin;
			meshInstance.Skeleton = meshInstance.GetPathTo(skeleton);
		}

		/// <summary>
		/// Allocate a generic type 2D array. The size is depending on the given parameters.
		/// </summary>		
		/// <param name="x">Defines the depth of the arrays first dimension</param>
		/// <param name="y">>Defines the depth of the arrays second dimension</param>
		/// <returns></returns>
		private static T[][] Allocate2dArray<T>(uint x, uint y)
		{
			var result = new T[x][];
			for (var i = 0; i < x; i++) result[i] = new T[y];
			return result;
		}

		/// <summary>
		/// Triggers loading, converting, and constructing of a UnityEngine.Mesh, and stores it in the asset cache
		/// </summary>
		/// <param name="mesh">The definition of the mesh to generate</param>
		/// <param name="meshIndex">The index of the mesh to generate</param>
		/// <param name="cancellationToken"></param>
		/// <returns>A task that completes when the mesh is attached to the given GameObject</returns>
		protected virtual async Task ConstructMesh(GLTFMesh mesh, int meshIndex, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (_assetCache.MeshCache[meshIndex] == null)
			{
				throw new Exception("Cannot generate mesh before ConstructMeshAttributes is called!");
			}
			else if (_assetCache.MeshCache[meshIndex].LoadedMesh != null)
			{
				return;
			}

			var primitiveCount = mesh.Primitives.Count;
			var firstPrim = mesh.Primitives[0];
			var meshCache = _assetCache.MeshCache[meshIndex];
			UnityMeshData unityData = new UnityMeshData()
			{
				Vertices = new Vector3[primitiveCount][],
				Normals = firstPrim.Attributes.ContainsKey(SemanticProperties.NORMAL) ? new Vector3[primitiveCount][] : null,
				Tangents = firstPrim.Attributes.ContainsKey(SemanticProperties.TANGENT) ? new float[primitiveCount][] : null,
				Uv1 = firstPrim.Attributes.ContainsKey(SemanticProperties.TEXCOORD_0) ? new Vector2[primitiveCount][] : null,
				Uv2 = firstPrim.Attributes.ContainsKey(SemanticProperties.TEXCOORD_1) ? new Vector2[primitiveCount][] : null,
				Colors = firstPrim.Attributes.ContainsKey(SemanticProperties.COLOR_0) ? new Color[primitiveCount][] : null,
				BoneWeights = firstPrim.Attributes.ContainsKey(SemanticProperties.WEIGHTS_0) ? new float[primitiveCount][] : null,
				Joints = firstPrim.Attributes.ContainsKey(SemanticProperties.JOINTS_0) ? new float[primitiveCount][] : null,

				MorphTargetVertices = firstPrim.Targets != null && firstPrim.Targets[0].ContainsKey(SemanticProperties.POSITION) ?
					new Vector3[primitiveCount, firstPrim.Targets.Count][] : null,
				MorphTargetNormals = firstPrim.Targets != null && firstPrim.Targets[0].ContainsKey(SemanticProperties.NORMAL) ?
					new Vector3[primitiveCount, firstPrim.Targets.Count][] : null,
				MorphTargetTangents = firstPrim.Targets != null && firstPrim.Targets[0].ContainsKey(SemanticProperties.TANGENT) ?
					new Vector3[primitiveCount, firstPrim.Targets.Count][] : null,

				Topology = new Godot.Mesh.PrimitiveType[primitiveCount],
				Indices = new int[primitiveCount][]
			};

			for (int i = 0; i < mesh.Primitives.Count; ++i)
			{
				var primitive = mesh.Primitives[i];
				var primCache = meshCache.Primitives[i];
				unityData.Topology[i] = GetTopology(primitive.Mode);

				if (IsMultithreaded)
				{
					await Task.Run(() => ConvertAttributeAccessorsToUnityTypes(primCache, unityData, i));
				}
				else
				{
					ConvertAttributeAccessorsToUnityTypes(primCache, unityData, i);
				}

				bool shouldUseDefaultMaterial = primitive.Material == null;

				GLTFMaterial materialToLoad = shouldUseDefaultMaterial ? DefaultMaterial : primitive.Material.Value;
				if ((shouldUseDefaultMaterial && _defaultLoadedMaterial == null) ||
					(!shouldUseDefaultMaterial && _assetCache.MaterialCache[primitive.Material.Id] == null))
				{
					await ConstructMaterial(materialToLoad, shouldUseDefaultMaterial ? -1 : primitive.Material.Id);
				}

				cancellationToken.ThrowIfCancellationRequested();

				if (unityData.Topology[i] == Godot.Mesh.PrimitiveType.Triangles && primitive.Indices != null && primitive.Indices.Value != null)
				{
					Statistics.TriangleCount += primitive.Indices.Value.Count / 3;
				}
				Statistics.VertexCount += unityData.Vertices[i].Length;
			}

			
			await ConstructUnityMesh(unityData, meshIndex, mesh.Name, primitiveCount);
		}

		protected void ConvertAttributeAccessorsToUnityTypes(
			MeshCacheData.PrimitiveCacheData primData,
			UnityMeshData unityData,
			int indexOffset)
		{
			// todo optimize: There are multiple copies being performed to turn the buffer data into mesh data. Look into reducing them
			var meshAttributes = primData.Attributes;
			int vertexCount = (int)meshAttributes[SemanticProperties.POSITION].AccessorId.Value.Count;

			var indices = meshAttributes.ContainsKey(SemanticProperties.INDICES)
				? meshAttributes[SemanticProperties.INDICES].AccessorContent.AsUInts.ToIntArrayRaw()
				: MeshPrimitive.GenerateIndices(vertexCount);
			if (unityData.Topology[indexOffset] == Godot.Mesh.PrimitiveType.Triangles)
				SchemaExtensions.FlipTriangleFaces(indices);
			unityData.Indices[indexOffset] = indices;

			if (meshAttributes.ContainsKey(SemanticProperties.Weight[0]))
			{
				unityData.BoneWeights[indexOffset] = meshAttributes[SemanticProperties.Weight[0]].AccessorContent.AsVec4s.ToFloat4Raw();
				float[] weights = unityData.BoneWeights[indexOffset];
				// normalize weights
				for (int i = 0; i < weights.Length; i += 4)
				{
					var weightSum = (weights[i] + weights[i + 1] + weights[i + 2] + weights[i + 3]);

					if (!Godot.Mathf.IsEqualApprox(weightSum, 0))
					{
						weights[i + 0] /= weightSum;
						weights[i + 1] /= weightSum;
						weights[i + 2] /= weightSum;
						weights[i + 3] /= weightSum;
					}
				}
			}
			if (meshAttributes.ContainsKey(SemanticProperties.Joint[0]))
			{
				unityData.Joints[indexOffset] = meshAttributes[SemanticProperties.Joint[0]].AccessorContent.AsVec4s.ToFloat4Raw();
			}

			if (meshAttributes.ContainsKey(SemanticProperties.POSITION))
			{
				unityData.Vertices[indexOffset] = meshAttributes[SemanticProperties.POSITION].AccessorContent.AsVertices.ToGodotVector3Raw();
			}
			if (meshAttributes.ContainsKey(SemanticProperties.NORMAL))
			{
				unityData.Normals[indexOffset] = meshAttributes[SemanticProperties.NORMAL].AccessorContent.AsNormals.ToGodotVector3Raw();
			}
			if (meshAttributes.ContainsKey(SemanticProperties.TANGENT))
			{
				unityData.Tangents[indexOffset] = meshAttributes[SemanticProperties.TANGENT].AccessorContent.AsTangents.ToFloat4Raw();
			}
			if (meshAttributes.ContainsKey(SemanticProperties.TexCoord[0]))
			{
				unityData.Uv1[indexOffset] = meshAttributes[SemanticProperties.TexCoord[0]].AccessorContent.AsTexcoords.ToGodotVector2Raw();
			}
			if (meshAttributes.ContainsKey(SemanticProperties.TexCoord[1]))
			{
				unityData.Uv2[indexOffset] = meshAttributes[SemanticProperties.TexCoord[1]].AccessorContent.AsTexcoords.ToGodotVector2Raw();
			}
			if (meshAttributes.ContainsKey(SemanticProperties.Color[0]))
			{
				unityData.Colors[indexOffset] = meshAttributes[SemanticProperties.Color[0]].AccessorContent.AsColors.ToGodotColorRaw();
			}
			var targets = primData.Targets;
			if (targets != null && targets.Count > 0)
			{
				for (int i = 0; i < targets.Count; ++i)
				{
					if (targets[i].ContainsKey(SemanticProperties.POSITION))
					{
						unityData.MorphTargetVertices[indexOffset, i] = targets[i][SemanticProperties.POSITION].AccessorContent.AsVec3s.ToGodotVector3Raw();
					}
					if (targets[i].ContainsKey(SemanticProperties.NORMAL))
					{
						unityData.MorphTargetNormals[indexOffset, i] = targets[i][SemanticProperties.NORMAL].AccessorContent.AsVec3s.ToGodotVector3Raw();
					}
					if (targets[i].ContainsKey(SemanticProperties.TANGENT))
					{
						unityData.MorphTargetTangents[indexOffset, i] = targets[i][SemanticProperties.TANGENT].AccessorContent.AsVec3s.ToGodotVector3Raw();
					}
				}
			}
		}

		protected virtual Task ConstructMaterialImageBuffers(GLTFMaterial def)
		{
			var tasks = new List<Task>(8);
			if (def.PbrMetallicRoughness != null)
			{
				var pbr = def.PbrMetallicRoughness;

				if (pbr.BaseColorTexture != null)
				{
					var textureId = pbr.BaseColorTexture.Index;
					tasks.Add(ConstructImageBuffer(textureId.Value, textureId.Id));
				}
				if (pbr.MetallicRoughnessTexture != null)
				{
					var textureId = pbr.MetallicRoughnessTexture.Index;

					tasks.Add(ConstructImageBuffer(textureId.Value, textureId.Id));
				}
			}

			if (def.CommonConstant != null)
			{
				if (def.CommonConstant.LightmapTexture != null)
				{
					var textureId = def.CommonConstant.LightmapTexture.Index;

					tasks.Add(ConstructImageBuffer(textureId.Value, textureId.Id));
				}
			}

			if (def.NormalTexture != null)
			{
				var textureId = def.NormalTexture.Index;
				tasks.Add(ConstructImageBuffer(textureId.Value, textureId.Id));
			}

			if (def.OcclusionTexture != null)
			{
				var textureId = def.OcclusionTexture.Index;

				if (!(def.PbrMetallicRoughness != null
						&& def.PbrMetallicRoughness.MetallicRoughnessTexture != null
						&& def.PbrMetallicRoughness.MetallicRoughnessTexture.Index.Id == textureId.Id))
				{
					tasks.Add(ConstructImageBuffer(textureId.Value, textureId.Id));
				}
			}

			if (def.EmissiveTexture != null)
			{
				var textureId = def.EmissiveTexture.Index;
				tasks.Add(ConstructImageBuffer(textureId.Value, textureId.Id));
			}

			// pbr_spec_gloss extension
			const string specGlossExtName = KHR_materials_pbrSpecularGlossinessExtensionFactory.EXTENSION_NAME;
			if (def.Extensions != null && def.Extensions.ContainsKey(specGlossExtName))
			{
				var specGlossDef = (KHR_materials_pbrSpecularGlossinessExtension)def.Extensions[specGlossExtName];
				if (specGlossDef.DiffuseTexture != null)
				{
					var textureId = specGlossDef.DiffuseTexture.Index;
					tasks.Add(ConstructImageBuffer(textureId.Value, textureId.Id));
				}

				if (specGlossDef.SpecularGlossinessTexture != null)
				{
					var textureId = specGlossDef.SpecularGlossinessTexture.Index;
					tasks.Add(ConstructImageBuffer(textureId.Value, textureId.Id));
				}
			}

			return Task.WhenAll(tasks);
		}

		/// <summary>
		/// Populate a UnityEngine.Mesh from preloaded and preprocessed buffer data
		/// </summary>
		/// <param name="meshConstructionData"></param>
		/// <param name="meshId"></param>
		/// <param name="primitiveIndex"></param>
		/// <param name="unityMeshData"></param>
		/// <returns></returns>
		protected async Task ConstructUnityMesh(UnityMeshData unityMeshData, int meshIndex, string meshName, int primitiveCount)
		{
			await YieldOnTimeoutAndThrowOnLowMemory();
			ArrayMesh mesh = new ArrayMesh
			{
				ResourceName = meshName,
			};

			for (int i = 0; i < primitiveCount; i++)
			{
				var array = new Godot.Collections.Array();
				array.Resize((int)ArrayMesh.ArrayType.Max);
				array[(int)ArrayMesh.ArrayType.Vertex] = unityMeshData.Vertices?[i] ?? null;
				await YieldOnTimeoutAndThrowOnLowMemory();
				array[(int)ArrayMesh.ArrayType.Normal] = unityMeshData.Normals?[i] ?? null;
				await YieldOnTimeoutAndThrowOnLowMemory();
				array[(int)ArrayMesh.ArrayType.Tangent] = unityMeshData.Tangents?[i] ?? null;
				await YieldOnTimeoutAndThrowOnLowMemory();
				array[(int)ArrayMesh.ArrayType.TexUv] = unityMeshData.Uv1?[i] ?? null;
				await YieldOnTimeoutAndThrowOnLowMemory();
				array[(int)ArrayMesh.ArrayType.TexUv2] = unityMeshData.Uv2?[i] ?? null;
				await YieldOnTimeoutAndThrowOnLowMemory();
				array[(int)ArrayMesh.ArrayType.Color] = unityMeshData.Colors?[i] ?? null;
				await YieldOnTimeoutAndThrowOnLowMemory();
				array[(int)ArrayMesh.ArrayType.Index] = unityMeshData.Indices?[i] ?? null;
				await YieldOnTimeoutAndThrowOnLowMemory();

				array[(int)ArrayMesh.ArrayType.Weights] = unityMeshData.BoneWeights?[i] ?? null;
				await YieldOnTimeoutAndThrowOnLowMemory();
				array[(int)ArrayMesh.ArrayType.Bones] = unityMeshData.Joints?[i] ?? null;
				await YieldOnTimeoutAndThrowOnLowMemory();

				Boolean generateTangents = unityMeshData.Topology[i] == Godot.Mesh.PrimitiveType.Triangles
					&& (array[(int)ArrayMesh.ArrayType.Tangent] == null)
					&& (array[(int)ArrayMesh.ArrayType.TexUv] != null)
					&& (array[(int)ArrayMesh.ArrayType.Normal] != null);

				void GenerateTangents(ref Godot.Collections.Array arr, Boolean deIndex = false)
				{
					var surfaceTool = new SurfaceTool();
					surfaceTool.Begin(Godot.Mesh.PrimitiveType.Triangles);
					if (arr[(int)ArrayMesh.ArrayType.Index] != null)
					{
						foreach (var index in (int[])arr[(int)ArrayMesh.ArrayType.Index])
							surfaceTool.AddIndex(index);
					}
					var vertexArray = (Vector3[])arr[(int)ArrayMesh.ArrayType.Vertex];
					var normalArray = (Vector3[])arr[(int)ArrayMesh.ArrayType.Normal];
					Vector2[] uv1Array = (Vector2[])arr[(int)ArrayMesh.ArrayType.TexUv];
					Vector2[] uv2Array;
					Vector3[] colorArray;
					float[] tangentArray;
					float[] boneArray;
					float[] weightArray;
					var hasColor = arr[(int)ArrayMesh.ArrayType.Color] != null;
					var hasUv2 = arr[(int)ArrayMesh.ArrayType.TexUv2] != null;
					var hasTangent = arr[(int)ArrayMesh.ArrayType.Tangent] != null;
					var hasBone = arr[(int)ArrayMesh.ArrayType.Bones] != null;
					var hasWeight = arr[(int)ArrayMesh.ArrayType.Weights] != null;

					if (hasColor)
						colorArray = (Vector3[])arr[(int)ArrayMesh.ArrayType.Color];
					uv1Array = (Vector2[])arr[(int)ArrayMesh.ArrayType.TexUv];
					if (hasUv2)
						uv2Array = (Vector2[])arr[(int)ArrayMesh.ArrayType.TexUv2];
					if (hasTangent)
						tangentArray = (float[])arr[(int)ArrayMesh.ArrayType.Tangent];
					if (hasBone)
						boneArray = (float[])arr[(int)ArrayMesh.ArrayType.Bones];
					if (hasWeight)
						weightArray = (float[])arr[(int)ArrayMesh.ArrayType.Weights];

					for (int i = 0; i < vertexArray.Length; i++)
					{
						surfaceTool.AddUv(((Vector2[])arr[(int)ArrayMesh.ArrayType.TexUv])[i]);
						if (hasUv2)
							surfaceTool.AddUv2(((Vector2[])arr[(int)ArrayMesh.ArrayType.TexUv2])[i]);
						if (hasColor)
							surfaceTool.AddColor(((Color[])arr[(int)ArrayMesh.ArrayType.Color])[i]);
						if (hasBone)
						{
							int[] bones = new int[4];
							bones[0] = (int)((float[])arr[(int)ArrayMesh.ArrayType.Bones])[i * 4 + 0];
							bones[1] = (int)((float[])arr[(int)ArrayMesh.ArrayType.Bones])[i * 4 + 1];
							bones[2] = (int)((float[])arr[(int)ArrayMesh.ArrayType.Bones])[i * 4 + 2];
							bones[3] = (int)((float[])arr[(int)ArrayMesh.ArrayType.Bones])[i * 4 + 3];
							surfaceTool.AddBones(bones);
						}
						if (hasWeight)
						{
							float[] weights = new float[4];
							weights[0] = ((float[])arr[(int)ArrayMesh.ArrayType.Weights])[i * 4 + 0];
							weights[1] = ((float[])arr[(int)ArrayMesh.ArrayType.Weights])[i * 4 + 1];
							weights[2] = ((float[])arr[(int)ArrayMesh.ArrayType.Weights])[i * 4 + 2];
							weights[3] = ((float[])arr[(int)ArrayMesh.ArrayType.Weights])[i * 4 + 3];
							surfaceTool.AddWeights(weights);
						}
						surfaceTool.AddNormal(((Vector3[])arr[(int)ArrayMesh.ArrayType.Normal])[i]);
						if (hasTangent)
							surfaceTool.AddTangent(new Plane(((float[])arr[(int)ArrayMesh.ArrayType.Tangent])[i * 4],
															((float[])arr[(int)ArrayMesh.ArrayType.Tangent])[i * 4 + 1],
															((float[])arr[(int)ArrayMesh.ArrayType.Tangent])[i * 4 + 2],
															((float[])arr[(int)ArrayMesh.ArrayType.Tangent])[i * 4 + 3]));
						surfaceTool.AddVertex(((Vector3[])arr[(int)ArrayMesh.ArrayType.Vertex])[i]);
					}
					var temp = surfaceTool.CommitToArrays();
					if (deIndex)
						surfaceTool.Deindex();
					surfaceTool.GenerateTangents();
					var newArray = surfaceTool.CommitToArrays();
					arr = newArray;
				}

				if (generateTangents)
				{
					GenerateTangents(ref array);
				}

				Godot.Collections.Array blendShapes = null;
				if (unityMeshData.MorphTargetVertices != null)
				{
					Godot.Collections.Array blendShapeArray = null;
					var firstPrim = _gltfRoot.Meshes[meshIndex].Primitives[0];
					blendShapes = new Godot.Collections.Array();
					mesh.BlendShapeMode = Godot.Mesh.BlendShapeMode.Normalized;

					for (int j = 0; j < firstPrim.Targets.Count; j++)
					{
						blendShapeArray = new Godot.Collections.Array();
						blendShapeArray.Resize((int)ArrayMesh.ArrayType.Max);
						for (int k = 0; k < (int)ArrayMesh.ArrayType.Max; k++)
						{
							blendShapeArray[k] = array[k];
						}
						if (i == 0)
						{
							var targetName = firstPrim.TargetNames != null ? firstPrim.TargetNames[j] : $"Morphtarget{j}";
							mesh.AddBlendShape(targetName);
						}

						if (unityMeshData.MorphTargetVertices?[i, j] != null)
						{
							Vector3[] srcArr = (Vector3[])array[(int)ArrayMesh.ArrayType.Vertex];
							int size = srcArr.Length;
							int maxIdx = unityMeshData.MorphTargetVertices[i, j].Length;
							var newArray = new Vector3[size];
							unityMeshData.MorphTargetVertices[i, j].CopyTo(newArray, 0);

							for (int l = 0; l < size; l++) {
								if (l < maxIdx) {
									newArray[l] = newArray[l] + srcArr[l];
								} else {
									newArray[l] = srcArr[l];
								}
							}
							blendShapeArray[(int)ArrayMesh.ArrayType.Vertex] = newArray;
						}
						await YieldOnTimeoutAndThrowOnLowMemory();
						if (unityMeshData.MorphTargetNormals?[i, j] != null)
						{
							Vector3[] srcArr = (Vector3[])array[(int)ArrayMesh.ArrayType.Normal];
							int size = srcArr.Length;
							int maxIdx = unityMeshData.MorphTargetNormals[i, j].Length;
							var newArray = new Vector3[size];

							for (int l = 0; l < size; l++) {
								if (l < maxIdx) {
									newArray[l] = unityMeshData.MorphTargetNormals[i, j][l] + srcArr[l];
								} else {
									newArray[l] = srcArr[l];
								}
							}
							blendShapeArray[(int)ArrayMesh.ArrayType.Normal] = newArray;
						}
						await YieldOnTimeoutAndThrowOnLowMemory();
						if (unityMeshData.MorphTargetTangents?[i, j] != null)
						{
							float[] srcArr = (float[])array[(int)ArrayMesh.ArrayType.Tangent];
							int size = srcArr.Length;
							var tangentVec3 = unityMeshData.MorphTargetTangents[i, j];
							int maxIdx = tangentVec3.Length;
							var newArray = new float[size];

							for (int l = 0; l < size / 4; l++) {

								if (l < maxIdx) {
									newArray[l * 4 + 0] = tangentVec3[l].x + srcArr[l * 4 + 0];
									newArray[l * 4 + 1] = tangentVec3[l].y + srcArr[l * 4 + 1];
									newArray[l * 4 + 2] = tangentVec3[l].z + srcArr[l * 4 + 2];
								} else {
									newArray[l * 4 + 0] = srcArr[l * 4 + 0];
									newArray[l * 4 + 1] = srcArr[l * 4 + 1];
									newArray[l * 4 + 2] = srcArr[l * 4 + 2];
								}
								newArray[l * 4 + 3] = srcArr[l * 4 + 3]; //copy flip value
							}
							blendShapeArray[(int)ArrayMesh.ArrayType.Tangent] = newArray;
						}
						await YieldOnTimeoutAndThrowOnLowMemory();
						blendShapeArray[(int)ArrayMesh.ArrayType.Index] = null;
						if (generateTangents)
						{
							GenerateTangents(ref blendShapeArray, true);
						}
						blendShapes.Add(blendShapeArray);
					}
				}
				await YieldOnTimeoutAndThrowOnLowMemory();
				mesh.AddSurfaceFromArrays(unityMeshData.Topology[i], array, blendShapes);
			}
			_assetCache.MeshCache[meshIndex].LoadedMesh = mesh;
		}

		protected virtual async Task ConstructMaterial(GLTFMaterial def, int materialIndex)
		{
			IUniformMap mapper;
			const string specGlossExtName = KHR_materials_pbrSpecularGlossinessExtensionFactory.EXTENSION_NAME;
			if (_gltfRoot.ExtensionsUsed != null && _gltfRoot.ExtensionsUsed.Contains(specGlossExtName)
				&& def.Extensions != null && def.Extensions.ContainsKey(specGlossExtName))
			{
				if (!string.IsNullOrEmpty(CustomShaderName))
				{
					mapper = new SpecGlossMap(CustomShaderName, MaximumLod);
				}
				else
				{
					mapper = new SpecGlossMap(MaximumLod);
				}
			}
			else
			{
				if (!string.IsNullOrEmpty(CustomShaderName))
				{
					mapper = new MetalRoughMap(CustomShaderName, MaximumLod);
				}
				else
				{
					mapper = new MetalRoughMap(MaximumLod);
				}
			}

			mapper.Material.ResourceName = def.Name;
			mapper.AlphaMode = def.AlphaMode;
			mapper.DoubleSided = def.DoubleSided;
			mapper.AlphaCutoff = def.AlphaCutoff;

			var mrMapper = mapper as IMetalRoughUniformMap;
			if (def.PbrMetallicRoughness != null && mrMapper != null)
			{
				var pbr = def.PbrMetallicRoughness;

				mrMapper.BaseColorFactor = pbr.BaseColorFactor.ToGodotColorRaw();

				if (pbr.BaseColorTexture != null)
				{
					TextureId textureId = pbr.BaseColorTexture.Index;
					await ConstructTexture(textureId.Value, textureId.Id, !KeepCPUCopyOfTexture, false);
					mrMapper.BaseColorTexture = _assetCache.TextureCache[textureId.Id].Texture;
					mrMapper.BaseColorTexCoord = pbr.BaseColorTexture.TexCoord;
					if (mrMapper.BaseColorFactor == null)
						mrMapper.BaseColorFactor = new Color(1, 1, 1);

					var ext = GetTextureTransform(pbr.BaseColorTexture);
					if (ext != null)
					{
						mrMapper.BaseColorXOffset = ext.Offset.ToUnityVector2Raw();
						mrMapper.BaseColorXRotation = ext.Rotation;
						mrMapper.BaseColorXScale = ext.Scale.ToUnityVector2Raw();
						mrMapper.BaseColorXTexCoord = ext.TexCoord;
					}
				}

				mrMapper.MetallicFactor = pbr.MetallicFactor;

				if (pbr.MetallicRoughnessTexture != null)
				{
					TextureId textureId = pbr.MetallicRoughnessTexture.Index;
					await ConstructTexture(textureId.Value, textureId.Id, !KeepCPUCopyOfTexture, true);
					mrMapper.MetallicRoughnessTexture = _assetCache.TextureCache[textureId.Id].Texture;
					mrMapper.MetallicRoughnessTexCoord = pbr.MetallicRoughnessTexture.TexCoord;

					var ext = GetTextureTransform(pbr.MetallicRoughnessTexture);
					if (ext != null)
					{
						mrMapper.MetallicRoughnessXOffset = ext.Offset.ToUnityVector2Raw();
						mrMapper.MetallicRoughnessXRotation = ext.Rotation;
						mrMapper.MetallicRoughnessXScale = ext.Scale.ToUnityVector2Raw();
						mrMapper.MetallicRoughnessXTexCoord = ext.TexCoord;
					}
				}

				mrMapper.RoughnessFactor = pbr.RoughnessFactor;
			}

			var sgMapper = mapper as ISpecGlossUniformMap;
			if (sgMapper != null)
			{
				var specGloss = def.Extensions[specGlossExtName] as KHR_materials_pbrSpecularGlossinessExtension;

				sgMapper.DiffuseFactor = specGloss.DiffuseFactor.ToGodotColorRaw();

				if (specGloss.DiffuseTexture != null)
				{
					TextureId textureId = specGloss.DiffuseTexture.Index;
					await ConstructTexture(textureId.Value, textureId.Id, !KeepCPUCopyOfTexture, false);
					sgMapper.DiffuseTexture = _assetCache.TextureCache[textureId.Id].Texture;
					sgMapper.DiffuseTexCoord = specGloss.DiffuseTexture.TexCoord;

					var ext = GetTextureTransform(specGloss.DiffuseTexture);
					if (ext != null)
					{
						sgMapper.DiffuseXOffset = ext.Offset.ToUnityVector2Raw();
						sgMapper.DiffuseXRotation = ext.Rotation;
						sgMapper.DiffuseXScale = ext.Scale.ToUnityVector2Raw();
						sgMapper.DiffuseXTexCoord = ext.TexCoord;
					}
				}

				sgMapper.SpecularFactor = specGloss.SpecularFactor.ToUnityVector3Raw();
				sgMapper.GlossinessFactor = specGloss.GlossinessFactor;

				if (specGloss.SpecularGlossinessTexture != null)
				{
					TextureId textureId = specGloss.SpecularGlossinessTexture.Index;
					await ConstructTexture(textureId.Value, textureId.Id, !KeepCPUCopyOfTexture, false);
					sgMapper.SpecularGlossinessTexture = _assetCache.TextureCache[textureId.Id].Texture;

					var ext = GetTextureTransform(specGloss.SpecularGlossinessTexture);
					if (ext != null)
					{
						sgMapper.SpecularGlossinessXOffset = ext.Offset.ToUnityVector2Raw();
						sgMapper.SpecularGlossinessXRotation = ext.Rotation;
						sgMapper.SpecularGlossinessXScale = ext.Scale.ToUnityVector2Raw();
						sgMapper.SpecularGlossinessXTexCoord = ext.TexCoord;
					}
				}
			}

			if (def.NormalTexture != null)
			{
				TextureId textureId = def.NormalTexture.Index;
				await ConstructTexture(textureId.Value, textureId.Id, !KeepCPUCopyOfTexture, true);
				mapper.NormalTexture = _assetCache.TextureCache[textureId.Id].Texture;
				mapper.NormalTexScale = def.NormalTexture.Scale;

				var ext = GetTextureTransform(def.NormalTexture);
				if (ext != null)
				{
					mapper.NormalXOffset = ext.Offset.ToUnityVector2Raw();
					mapper.NormalXRotation = ext.Rotation;
					mapper.NormalXScale = ext.Scale.ToUnityVector2Raw();
					mapper.NormalXTexCoord = ext.TexCoord;
				}
			}

			if (def.OcclusionTexture != null)
			{
				mapper.OcclusionTexStrength = def.OcclusionTexture.Strength;
				TextureId textureId = def.OcclusionTexture.Index;
				await ConstructTexture(textureId.Value, textureId.Id, !KeepCPUCopyOfTexture, true);
				mapper.OcclusionTexture = _assetCache.TextureCache[textureId.Id].Texture;
				mapper.OcclusionTexCoord = def.OcclusionTexture.TexCoord;

				var ext = GetTextureTransform(def.OcclusionTexture);
				if (ext != null)
				{
					mapper.OcclusionXOffset = ext.Offset.ToUnityVector2Raw();
					mapper.OcclusionXRotation = ext.Rotation;
					mapper.OcclusionXScale = ext.Scale.ToUnityVector2Raw();
					mapper.OcclusionXTexCoord = ext.TexCoord;
				}
			}

			mapper.EmissiveFactor = def.EmissiveFactor.ToGodotColorRaw();
			if (def.EmissiveTexture != null)
			{
				TextureId textureId = def.EmissiveTexture.Index;
				await ConstructTexture(textureId.Value, textureId.Id, !KeepCPUCopyOfTexture, false);
				mapper.EmissiveTexture = _assetCache.TextureCache[textureId.Id].Texture;
				mapper.EmissiveTexCoord = def.EmissiveTexture.TexCoord;

				var ext = GetTextureTransform(def.EmissiveTexture);
				if (ext != null)
				{
					mapper.EmissiveXOffset = ext.Offset.ToUnityVector2Raw();
					mapper.EmissiveXRotation = ext.Rotation;
					mapper.EmissiveXScale = ext.Scale.ToUnityVector2Raw();
					mapper.EmissiveXTexCoord = ext.TexCoord;
				}
			}


			var vertColorMapper = mapper.Clone();
			vertColorMapper.VertexColorsEnabled = true;

			MaterialCacheData materialWrapper = new MaterialCacheData
			{
				UnityMaterial = mapper.Material,
				UnityMaterialWithVertexColor = vertColorMapper.Material,
				GLTFMaterial = def
			};

			if (materialIndex >= 0)
			{
				_assetCache.MaterialCache[materialIndex] = materialWrapper;
			}
			else
			{
				_defaultLoadedMaterial = materialWrapper;
			}
		}

		protected virtual int GetTextureSourceId(GLTFTexture texture)
		{
			return texture.Source.Id;
		}

		/// <summary>
		/// Creates a texture from a glTF texture
		/// </summary>
		/// <param name="texture">The texture to load</param>
		/// <param name="textureIndex">The index in the texture cache</param>
		/// <param name="markGpuOnly">Whether the texture is GPU only, instead of keeping a CPU copy</param>
		/// <param name="isLinear">Whether the texture is linear rather than sRGB</param>
		/// <returns>The loading task</returns>
		public virtual async Task LoadTextureAsync(GLTFTexture texture, int textureIndex, bool markGpuOnly, bool isLinear)
		{
			try
			{
				lock (this)
				{
					if (_isRunning)
					{
						throw new GLTFLoadException("Cannot CreateTexture while GLTFSceneImporter is already running");
					}

					_isRunning = true;
				}

				if (_options.ThrowOnLowMemory)
				{
					_memoryChecker = new MemoryChecker();
				}

				if (_gltfRoot == null)
				{
					await LoadJson(_gltfFileName);
				}

				if (_assetCache == null)
				{
					_assetCache = new AssetCache(_gltfRoot);
				}

				await ConstructImageBuffer(texture, textureIndex);
				await ConstructTexture(texture, textureIndex, markGpuOnly, isLinear);
			}
			finally
			{
				lock (this)
				{
					_isRunning = false;
				}
			}
		}

		public virtual Task LoadTextureAsync(GLTFTexture texture, int textureIndex, bool isLinear)
		{
			return LoadTextureAsync(texture, textureIndex, !KeepCPUCopyOfTexture, isLinear);
		}

		/// <summary>
		/// Gets texture that has been loaded from CreateTexture
		/// </summary>
		/// <param name="textureIndex">The texture to get</param>
		/// <returns>Created texture</returns>
		public virtual Texture GetTexture(int textureIndex)
		{
			if (_assetCache == null)
			{
				throw new GLTFLoadException("Asset cache needs initialized before calling GetTexture");
			}

			if (_assetCache.TextureCache[textureIndex] == null)
			{
				return null;
			}

			return _assetCache.TextureCache[textureIndex].Texture;
		}

		protected virtual async Task ConstructTexture(GLTFTexture texture, int textureIndex,
			bool markGpuOnly, bool isLinear)
		{
			if (_assetCache.TextureCache[textureIndex].Texture == null)
			{
				int sourceId = GetTextureSourceId(texture);
				GLTFImage image = _gltfRoot.Images[sourceId];
				await ConstructImage(image, sourceId, markGpuOnly, isLinear);

				var source = _assetCache.ImageCache[sourceId];
				_assetCache.TextureCache[textureIndex].Texture = source;

				/* Sampler not support in godot
				FilterMode desiredFilterMode;
				TextureWrapMode desiredWrapMode;

				if (texture.Sampler != null)
				{
					var sampler = texture.Sampler.Value;
					switch (sampler.MinFilter)
					{
						case MinFilterMode.Nearest:
						case MinFilterMode.NearestMipmapNearest:
						case MinFilterMode.LinearMipmapNearest:
							desiredFilterMode = FilterMode.Point;
							break;
						case MinFilterMode.Linear:
						case MinFilterMode.NearestMipmapLinear:
							desiredFilterMode = FilterMode.Bilinear;
							break;
						case MinFilterMode.LinearMipmapLinear:
							desiredFilterMode = FilterMode.Trilinear;
							break;
						default:
							Debug.LogWarning("Unsupported Sampler.MinFilter: " + sampler.MinFilter);
							desiredFilterMode = FilterMode.Trilinear;
							break;
					}

					switch (sampler.WrapS)
					{
						case GLTF.Schema.WrapMode.ClampToEdge:
							desiredWrapMode = TextureWrapMode.Clamp;
							break;
						case GLTF.Schema.WrapMode.Repeat:
							desiredWrapMode = TextureWrapMode.Repeat;
							break;
						case GLTF.Schema.WrapMode.MirroredRepeat:
							desiredWrapMode = TextureWrapMode.Mirror;
							break;
						default:
							Debug.LogWarning("Unsupported Sampler.WrapS: " + sampler.WrapS);
							desiredWrapMode = TextureWrapMode.Repeat;
							break;
					}
				}
				else
			
				{
					desiredFilterMode = FilterMode.Trilinear;
					desiredWrapMode = TextureWrapMode.Repeat;
				}

				var matchSamplerState = source.filterMode == desiredFilterMode && source.wrapMode == desiredWrapMode;
				if (matchSamplerState || markGpuOnly)
				{
					Debug.Assert(_assetCache.TextureCache[textureIndex].Texture == null, "Texture should not be reset to prevent memory leaks");
					_assetCache.TextureCache[textureIndex].Texture = source;

					if (!matchSamplerState)
					{
						GD.Print($"Ignoring sampler; filter mode: source {source.filterMode}, desired {desiredFilterMode}; wrap mode: source {source.wrapMode}, desired {desiredWrapMode}");
					}
				}
				else
				{
					var unityTexture = Object.Instantiate(source);
					unityTexture.filterMode = desiredFilterMode;
					unityTexture.wrapMode = desiredWrapMode;

					Debug.Assert(_assetCache.TextureCache[textureIndex].Texture == null, "Texture should not be reset to prevent memory leaks");
					_assetCache.TextureCache[textureIndex].Texture = unityTexture;
				}
				*/
			}
		}
#if false // FIXME
		protected virtual void ConstructImageFromGLB(GLTFImage image, int imageCacheIndex)
		{
			var texture = new Texture2D(0, 0);
			texture.name = nameof(GLTFSceneImporter) + (image.Name != null ? ("." + image.Name) : "");
			var bufferView = image.BufferView.Value;
			var data = new byte[bufferView.ByteLength];

			var bufferContents = _assetCache.BufferCache[bufferView.Buffer.Id];
			bufferContents.Stream.Position = bufferView.ByteOffset + bufferContents.ChunkOffset;
			bufferContents.Stream.Read(data, 0, data.Length);
			texture.LoadImage(data);

			Debug.Assert(_assetCache.ImageCache[imageCacheIndex] == null, "ImageCache should not be loaded multiple times");
			progressStatus.TextureLoaded++;
			progress?.Report(progressStatus);
			_assetCache.ImageCache[imageCacheIndex] = texture;

		}
#endif
		protected virtual BufferCacheData ConstructBufferFromGLB(int bufferIndex)
		{
			GLTFParser.SeekToBinaryChunk(_gltfStream.Stream, bufferIndex, _gltfStream.StartPosition);  // sets stream to correct start position
			return new BufferCacheData
			{
				Stream = _gltfStream.Stream,
				ChunkOffset = (uint)_gltfStream.Stream.Position
			};
		}

		protected virtual ExtTextureTransformExtension GetTextureTransform(TextureInfo def)
		{
			IExtension extension;
			if (_gltfRoot.ExtensionsUsed != null &&
				_gltfRoot.ExtensionsUsed.Contains(ExtTextureTransformExtensionFactory.EXTENSION_NAME) &&
				def.Extensions != null &&
				def.Extensions.TryGetValue(ExtTextureTransformExtensionFactory.EXTENSION_NAME, out extension))
			{
				return (ExtTextureTransformExtension)extension;
			}
			else return null;
		}

		protected async Task YieldOnTimeoutAndThrowOnLowMemory()
		{
			if (_options.ThrowOnLowMemory)
			{
				_memoryChecker.ThrowIfOutOfMemory();
			}

			if (_options.AsyncCoroutineHelper != null)
			{
				await _options.AsyncCoroutineHelper.YieldOnTimeout();
			}
		}
#if false // FIXME

		/// <summary>
		///	 Get the absolute path to a gltf uri reference.
		/// </summary>
		/// <param name="gltfPath">The path to the gltf file</param>
		/// <returns>A path without the filename or extension</returns>
		protected static string AbsoluteUriPath(string gltfPath)
		{
			var uri = new Uri(gltfPath);
			var partialPath = uri.AbsoluteUri.Remove(uri.AbsoluteUri.Length - uri.Query.Length - uri.Segments[uri.Segments.Length - 1].Length);
			return partialPath;
		}

		/// <summary>
		/// Get the absolute path a gltf file directory
		/// </summary>
		/// <param name="gltfPath">The path to the gltf file</param>
		/// <returns>A path without the filename or extension</returns>
		protected static string AbsoluteFilePath(string gltfPath)
		{
			var fileName = Path.GetFileName(gltfPath);
			var lastIndex = gltfPath.IndexOf(fileName);
			var partialPath = gltfPath.Substring(0, lastIndex);
			return partialPath;
		}
#endif
		protected static Godot.Mesh.PrimitiveType GetTopology(DrawMode mode)
		{
			switch (mode)
			{
				case DrawMode.Points: return Godot.Mesh.PrimitiveType.Points;
				case DrawMode.Lines: return Godot.Mesh.PrimitiveType.Lines;
				case DrawMode.LineStrip: return Godot.Mesh.PrimitiveType.LineStrip;
				case DrawMode.Triangles: return Godot.Mesh.PrimitiveType.Triangles;
				case DrawMode.TriangleStrip: return Godot.Mesh.PrimitiveType.TriangleStrip;
			}

			throw new Exception("Unity does not support glTF draw mode: " + mode);
		}

		/// <summary>
		/// Cleans up any undisposed streams after loading a scene or a node.
		/// </summary>
		private void Cleanup()
		{
			if (_assetCache != null)
			{
				_assetCache.Dispose();
				_assetCache = null;
			}
		}


		private async Task SetupLoad(Func<Task> callback)
		{
			try
			{
				lock (this)
				{
					if (_isRunning)
					{
						throw new GLTFLoadException("Cannot start a load while GLTFSceneImporter is already running");
					}

					_isRunning = true;
				}

				Statistics = new ImportStatistics();
				if (_options.ThrowOnLowMemory)
				{
					_memoryChecker = new MemoryChecker();
				}

				if (_gltfRoot == null)
				{
					await LoadJson(_gltfFileName);
				}

				if (_assetCache == null)
				{
					_assetCache = new AssetCache(_gltfRoot);
				}

				await callback();
			}
			catch
			{
				Cleanup();
				throw;
			}
			finally
			{
				lock (this)
				{
					_isRunning = false;
				}
			}
		}
	}
}
