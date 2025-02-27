﻿
using GodotGLTF.Loader;

namespace GodotGLTF
{
	public abstract class ImporterFactory// : ScriptableObject //FIXME
	{
		public abstract GLTFSceneImporter CreateSceneImporter(string gltfFileName, ImportOptions options);
	}

	public class DefaultImporterFactory : ImporterFactory
	{
		public override GLTFSceneImporter CreateSceneImporter(string gltfFileName, ImportOptions options)
		{
			return new GLTFSceneImporter(gltfFileName, options);
		}
	}
}
