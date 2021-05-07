using GLTF.Schema;
using System;
using Godot;

namespace UnityGLTF.Cache
{
	public class MaterialCacheData : IDisposable
	{
		public Material UnityMaterial { get; set; }
		public Material UnityMaterialWithVertexColor { get; set; }
		public GLTFMaterial GLTFMaterial { get; set; }

		public Material GetContents(bool useVertexColors)
		{
			return useVertexColors ? UnityMaterialWithVertexColor : UnityMaterial;
		}

		/// <summary>
		/// Unloads the materials in this cache.
		/// </summary>
		public void Dispose()
		{
			if (UnityMaterial != null)
			{
				UnityMaterial.Free();
				UnityMaterial = null;
			}

			if (UnityMaterialWithVertexColor != null)
			{
				UnityMaterialWithVertexColor.Free();
				UnityMaterialWithVertexColor = null;
			}
		}
	}
}
