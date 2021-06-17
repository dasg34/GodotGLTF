using System;
using GLTF.Schema;
using Godot;
using Material = Godot.Material;
using Texture = Godot.Texture;

namespace GodotGLTF
{
	public class StandardMap : IUniformMap
	{
		protected SpatialMaterial _material;
		private AlphaMode _alphaMode = AlphaMode.OPAQUE;
		private double _alphaCutoff = 0.5;
		private int emissiveTexCoord = 0;
		private int occlusionTexCoord = 0;

		private Vector2 normalOffset = new Vector2(0, 0);
		private Vector2 occlusionOffset = new Vector2(0, 0);
		private Vector2 emissiveOffset = new Vector2(0, 0);

		protected StandardMap(string shaderName, int MaxLOD = 1000)
		{
			_material = new SpatialMaterial();
			_material.ResourceName = shaderName;
		}

		protected StandardMap(Material mat, int MaxLOD = 1000)
		{
			_material = mat as SpatialMaterial;
		}

		public Material Material { get { return _material; } }

		public virtual Texture NormalTexture
		{
			get { return _material.NormalTexture; }
			set
			{
				_material.NormalTexture = value;
				_material.NormalEnabled = true;
			}
		}

		// not implemented by the Standard shader
		public virtual int NormalTexCoord
		{
			get { return 0; }
			set { return; }
		}

		public virtual Vector2 NormalXOffset
		{
			get { return normalOffset; }
			set
			{
				normalOffset = value;
			}
		}

		public virtual double NormalXRotation
		{
			get { return 0; }
			set { return; }
		}

		public virtual Vector2 NormalXScale
		{
			get;
			set;
		}

		public virtual int NormalXTexCoord
		{
			get { return 0; }
			set { return; }
		}

		public virtual double NormalTexScale
		{
			get { return _material.NormalScale; }
			set
			{
				_material.NormalScale = Convert.ToSingle(value);
			}
		}

		public virtual Texture OcclusionTexture
		{
			get { return _material.AoTexture; }
			set
			{
				_material.AoTexture = value;
				_material.AoTextureChannel = SpatialMaterial.TextureChannel.Red;
				_material.AoEnabled = true;
			}
		}

		public virtual int OcclusionTexCoord
		{
			get => occlusionTexCoord;
			set
			{
				occlusionTexCoord = value;
				if (occlusionTexCoord == 1)
				{
					_material.AoOnUv2 = true;
				}
			}
		}

		public virtual Vector2 OcclusionXOffset
		{
			get;
			set;
		}

		public virtual double OcclusionXRotation
		{
			get { return 0; }
			set { return; }
		}

		public virtual Vector2 OcclusionXScale
		{
			get;
			set;
		}

		public virtual int OcclusionXTexCoord
		{
			get { return 0; }
			set { return; }
		}

		// Not support in godot
		public virtual double OcclusionTexStrength
		{
			get;
			set;
		}

		public virtual Texture EmissiveTexture
		{
			get { return _material.EmissionTexture; }
			set
			{
				_material.EmissionTexture = value;
				_material.EmissionEnabled = true;
				_material.Emission = new Color(0, 0, 0);
			}
		}

		// not implemented by the Standard shader
		public virtual int EmissiveTexCoord
		{
			get => emissiveTexCoord;
			set
			{
				emissiveTexCoord = value;
				if (emissiveTexCoord == 1)
				{
					_material.EmissionOnUv2 = true;
				}
			}
		}

		public virtual Vector2 EmissiveXOffset
		{
			get;
			set;
		}

		public virtual double EmissiveXRotation
		{
			get { return 0; }
			set { return; }
		}

		public virtual Vector2 EmissiveXScale
		{
			get;
			set;
		}

		public virtual int EmissiveXTexCoord
		{
			get { return 0; }
			set { return; }
		}

		public virtual Color EmissiveFactor
		{
			get { return _material.Emission; }
			set
			{
				_material.EmissionEnabled = true;
				_material.Emission = value;
			}
		}

		public virtual AlphaMode AlphaMode
		{
			get { return _alphaMode; }
			set
			{
				if (value == AlphaMode.MASK)
				{
					_material.ParamsUseAlphaScissor = true;
				}
				else if (value == AlphaMode.BLEND)
				{
					_material.FlagsTransparent = true;
					_material.ParamsDepthDrawMode = SpatialMaterial.DepthDrawMode.AlphaOpaquePrepass;
				}

				_alphaMode = value;
			}
		}

		public virtual double AlphaCutoff
		{
			get { return _alphaCutoff; }
			set
			{
				if (_alphaMode == AlphaMode.MASK)
				{
					_material.ParamsAlphaScissorThreshold = (float)value;
				}
				_alphaCutoff = value;
			}
		}

		public virtual bool DoubleSided
		{
			get { return _material.ParamsCullMode == SpatialMaterial.CullMode.Disabled; }
			set
			{
				if (value)
					_material.ParamsCullMode = SpatialMaterial.CullMode.Disabled;
			}
		}

		//not used in godot.
		public virtual bool VertexColorsEnabled
		{
			get;
			set;
		}

		public virtual IUniformMap Clone()
		{
			var ret = new StandardMap(_material);
			ret._alphaMode = _alphaMode;
			ret._alphaCutoff = _alphaCutoff;
			return ret;
		}

		protected virtual void Copy(IUniformMap o)
		{
			var other = (StandardMap)o;
			other._material = _material;
			other._alphaCutoff = _alphaCutoff;
			other._alphaMode = _alphaMode;
		}
	}
}
