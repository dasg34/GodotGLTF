using Godot;

namespace GodotGLTF
{
	public class SpecGloss2StandardMap : StandardMap, ISpecGlossUniformMap
	{
		private Vector2 diffuseOffset = new Vector2(0, 0);
		private Vector2 specGlossOffset = new Vector2(0, 0);

		public SpecGloss2StandardMap(int MaxLOD = 1000) : base("Standard (Specular setup)", MaxLOD) { }
		protected SpecGloss2StandardMap(string shaderName, int MaxLOD = 1000) : base(shaderName, MaxLOD) { }
		protected SpecGloss2StandardMap(Material m, int MaxLOD = 1000) : base(m, MaxLOD) { }

		public virtual Texture DiffuseTexture
		{
			get { return _material.AlbedoTexture; }
			set { _material.AlbedoTexture = value; }
		}

		// not implemented by the Standard shader
		public virtual int DiffuseTexCoord
		{
			get { return 0; }
			set { return; }
		}

		// Not support in godot
		public virtual Vector2 DiffuseXOffset
		{
			get { return diffuseOffset; }
			set
			{
				diffuseOffset = value;
				var unitySpaceVec = new Vector2(diffuseOffset.x, 1 - DiffuseXScale.y - diffuseOffset.y);
			}
		}

		public virtual double DiffuseXRotation
		{
			get { return 0; }
			set { return; }
		}

		// Not support in godot
		public virtual Vector2 DiffuseXScale
		{
			get;
			set;
		}

		public virtual int DiffuseXTexCoord
		{
			get { return 0; }
			set { return; }
		}

		public virtual Color DiffuseFactor
		{
			get { return _material.AlbedoColor; }
			set { _material.AlbedoColor = value; }
		}

		public virtual Texture SpecularGlossinessTexture
		{
			get { return _material.AlbedoTexture; }
			set
			{
				_material.AlbedoTexture = value;
			}
		}

		// not implemented by the Standard shader
		public virtual int SpecularGlossinessTexCoord
		{
			get { return 0; }
			set { return; }
		}

		// Not support in godot
		public virtual Vector2 SpecularGlossinessXOffset
		{
			get { return specGlossOffset; }
			set
			{
				specGlossOffset = value;
				var unitySpaceVec = new Vector2(specGlossOffset.x, 1 - SpecularGlossinessXScale.y - specGlossOffset.y);
			}
		}

		public virtual double SpecularGlossinessXRotation
		{
			get { return 0; }
			set { return; }
		}

		// Not support in godot
		public virtual Vector2 SpecularGlossinessXScale
		{
			get;
			set;
		}

		public virtual int SpecularGlossinessXTexCoord
		{
			get { return 0; }
			set { return; }
		}

		public virtual Vector3 SpecularFactor
		{
			get;
			set;
		}

		public virtual double GlossinessFactor
		{
			get { return _material.Roughness; }
			set
			{
				_material.Roughness = 1 - Mathf.Clamp((float)value, 0.0f, 1.0f);
			}
		}

		public override IUniformMap Clone()
		{
			var copy = new SpecGloss2StandardMap(_material);
			base.Copy(copy);
			return copy;
		}
	}
}
