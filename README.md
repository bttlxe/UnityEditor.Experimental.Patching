#  Unity + Git Submodules + VS2022 FAQ

Visual Studio 2022 supports native Git submodule handling - but Unity's default `.csproj` file generation is incompatible with it.

This repo contains a Unity Editor script that works around this issue by restructuring `.csproj` and `.sln` files so Visual Studio 2022 can load and use them properly, while remaining fully compatible with Unity and Git.



---

## Why doesn't Unity's default structure work with Git submodules in VS2022?

Visual Studio 2022 requires all referenced `.csproj` files to exist within the same Git submodule directory tree in order to work with its native Git UI and project system. However, Unity generates all `.csproj` files in the root of the main project, which breaks this assumption and prevents Visual Studio from seeing them correctly within submodules.

---

## What does this Unity Editor script do?

This script:

- Uses `AssetPostprocessor.OnGeneratedSlnSolution` and `OnGeneratedCSProject` to hook into Unity's project file generation.
- Creates hard links to Unity-generated `.csproj` files from the directories containing their corresponding `.asmdef` files.
- Rewrites the `.sln` file to use these hard links instead of the originals.
- Ensures paths in the `.csproj` files are shown relative to the submodule project file, not the Unity project root - improving Visual Studio’s Solution Explorer layout.

This keeps Unity happy, Git clean, and Visual Studio organised.

---

## Where should I place this script?
This repo contains its own `.asmdef` file and can be placed anywhere Unity can find it in your project.

> You can use this repo as a Git submodule too!

---

## What about assemblies from packages or the Library folder?

Assemblies that are part of imported Unity packages - such as those found under `Library/PackageCache/` - are ignored. The script only rewrites `.csproj` files generated for source assemblies in your actual Unity project and submodules.

---

## Why use hard links instead of copying files?

Hard links ensure the `.csproj` file appears in the submodule’s directory without duplicating content. Visual Studio sees a project file within the submodule, and Unity continues to manage the original. Since hard links point to the same data, there's no risk of desync or duplication.

---

## What are the side effects?

The only notable side effect is that Visual Studio will create its working `Temp` and `obj` folders next to each hard-linked `.csproj` file - i.e., inside submodule folders. These should be excluded from Git using `.gitignore` (which is best practice when working with Unity projects in Git).

Example `.gitignore`:
```
[Tt]emp/
[Oo]bj/
```

---

##  Will this affect other developers?

No.  You shouldn't be tracking `.csproj` or `.sln` files in Git with Unity anyway as they are automatically generated, so this script will have no effect on anything that is tracked or used by other developers.

---

##  Can this break anything?

This script only affects how solution and project files are generated locally. It does not affect runtime behavior, game logic, or the Unity project itself.

Be sure to test your development and build pipelines after enabling this setup.  Please raise an issue if anything breaks!

---

##  Pro tip

Do not track the `.sln` and `.csproj` files. Let Unity regenerate them on each developer machine as needed.

---
