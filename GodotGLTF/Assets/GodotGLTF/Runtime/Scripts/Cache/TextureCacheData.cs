using GLTF.Schema;
using System;
using Godot;

namespace GodotGLTF.Cache
{
	public class TextureCacheData : IDisposable
	{
		public GLTFTexture TextureDefinition;
		public Texture Texture;

		/// <summary>
		/// Unloads the textures in this cache.
		/// </summary>
		public void Dispose()
		{
			if (Texture != null)
			{
				Texture.Free();
				Texture = null;
			}
		}
	}
}
