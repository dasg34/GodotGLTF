namespace GLTF
{
	public class AttributeAccessorSparse
	{
		public System.IO.Stream ValueStream { get; set; }
		public uint ValueOffset { get; set; }
		public System.IO.Stream IndicesStream { get; set; }
		public uint IndicesOffset { get; set; }
	}
}
