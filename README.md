# GodotGLTF

GodotSharp library for importing and exporting [GLTF 2.0](https://github.com/KhronosGroup/glTF/) assets.

The goal of this library is to support the full glTF 2.0 specification and enable the following scenarios:
- Run-time import of glTF 2.0 files
- Run-time export of glTF 2.0 files (Not available yet)
- Design-time import of glTF 2.0 files (Not available yet)
- Design-time export of glTF 2.0 files (Not available yet)

The library will be modularized such that it can be extended to support additional capabilities in Godot or support additional extensions to the glTF specification.  The library was designed to work with Godot 3.3, but is currently only tested/maintained/supported with Godot 3.3.2.

## Current Status

 -

## Getting Started

- This section is dedicated to those who wish to contribute to the project. This should clarify the main project structure without flooding you with too many details.
- GodotGLTF project is divided into two parts: the GLTFSerializer (.Net Standard Library), and the GodotGLTF Project (which is the package we wish to make available to users).

### [GLTFSerializer](https://github.com/dasg34/GodotGLTF/tree/master/GLTFSerialization)

- **Basic Rundown**: The GLTFSerializer is a C# DLL implemented to facilitate serialization of the Unity asset model, and deserialization of GLTF assets.

- **Structure**: 
	- Each GLTF schemas (Buffer, Accessor, Camera, Image...) extends the basic class: GLTFChildOfRootProperty. Through this object model, each schema can have its own defined serialization/deserialization functionalities, which imitate the JSON file structure as per the GLTF specification.
	- Each schema can then be grouped under the GLTFRoot object, which represents the underlying GLTF Asset. Serializing the asset is then done by serializing the root object, which recursively serializes all individual schemas. Deserializing a GLTF asset is done similarly: instantiate a GLTFRoot, and parse the required schemas.
- **Building**: You will need to build this library: 
	1. Open `GLTFSerialization\GLTFSerialization.sln` and compile for release. This will put the binaries in `GodotGLTF/Libraries`.
	2. Build GodotGLTF project.

### [The GodotGLTF Project](https://github.com/dasg34/GodotGLTF/tree/master/GodotGLTF)

- **GodotEngine Version**
	GodotGLTF is currently only tested/maintained/supported with Godot 3.3.2.
	You can download the 3.3.2 version [here](https://downloads.tuxfamily.org/godotengine/3.3.2/).

### The Server-Side Build

- Not available

## Examples

- Not available


