using System;
using Godot;
using AlphaMode = GLTF.Schema.AlphaMode;

namespace GodotGLTF
{
	public class MetalRoughMap : MetalRough2StandardMap
	{
		private Vector2 metalRoughOffset = new Vector2(0, 0);

		public MetalRoughMap(int MaxLOD = 1000) : base("GLTF/PbrMetallicRoughness", MaxLOD) { }
		public MetalRoughMap(string shaderName, int MaxLOD = 1000) : base(shaderName, MaxLOD) { }
		protected MetalRoughMap(Material m, int MaxLOD = 1000) : base(m, MaxLOD) { }

		public override int NormalTexCoord
		{
			get { return 0; }
			set { return; }
		}

		public override int BaseColorTexCoord
		{
			get { return 0; }
			set { return; }
		}

		public override Texture MetallicRoughnessTexture
		{
			get { return _material.MetallicTexture; /* FIXME.. return with RoughnessTexture?*/ }
			set
			{
				_material.MetallicTexture = value;
				_material.MetallicTextureChannel = SpatialMaterial.TextureChannel.Blue;
				_material.RoughnessTexture = value;
				_material.RoughnessTextureChannel = SpatialMaterial.TextureChannel.Green;
			}
		}

		public override int MetallicRoughnessTexCoord
		{
			get { return 0; }
			set { return; }
		}

		//Not support in godot
		public override Vector2 MetallicRoughnessXOffset
		{
			get { return metalRoughOffset; }
			set
			{

				metalRoughOffset = value;
			}
		}

		public override double MetallicRoughnessXRotation
		{
			get { return 0; }
			set { return; }
		}

		//Not support in godot
		public override Vector2 MetallicRoughnessXScale
		{
			get { return new Vector2(); }
			set
			{
			}
		}

		public override int MetallicRoughnessXTexCoord
		{
			get { return 0; }
			set { return; }
		}

		public override double RoughnessFactor
		{
			get { return _material.Roughness; }
			set { _material.Roughness = (float)value; }
		}

		public override IUniformMap Clone()
		{
			var copy = new MetalRoughMap(_material);
			base.Copy(copy);
			return copy;
		}
	}
}
