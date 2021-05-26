using System.IO;

namespace GodotGLTF.Loader
{
	public interface IDataLoader2 : IDataLoader
	{
		Stream LoadStream(string relativeFilePath);
	}
}
