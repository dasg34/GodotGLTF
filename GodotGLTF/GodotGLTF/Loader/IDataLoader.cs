using System.IO;
using System.Threading.Tasks;

namespace GodotGLTF.Loader
{
	public interface IDataLoader
	{
		Task<Stream> LoadStreamAsync(string relativeFilePath);
	}
}
