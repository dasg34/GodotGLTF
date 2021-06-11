using GLTF;
using GLTF.Schema;

namespace GodotGLTF.Cache
{
	public struct AnimationSamplerCacheData
	{
		public AttributeAccessor Input;
		public AttributeAccessor Output;
		public InterpolationType Interpolation;
	}

	public class AnimationCacheData
	{
		public Godot.Animation LoadedAnimationClip { get; set; }
		public AnimationSamplerCacheData[] Samplers { get; set; }

		public AnimationCacheData(int samplerCount)
		{
			Samplers = new AnimationSamplerCacheData[samplerCount];
		}
	}
}
