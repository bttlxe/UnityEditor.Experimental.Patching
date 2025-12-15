#  Unity + Git Submodules + Visual Studio 2022/2026 FAQ

Visual Studio 2022 onwards supports native Git submodule handling - but Unity's default `.csproj` file generation is incompatible with it.

This repo contains a Unity Editor script that works around this issue by restructuring generated project and solution files so Visual Studio 2022 and 2026 can load and use them properly, while remaining fully compatible with Unity and Git.

---

## Why doesn't Unity's default structure work with Git submodules in VS2022/2026?

Visual Studio 2022/2026 requires all referenced `.csproj` files to exist within the same Git submodule directory tree in order to work with its native Git UI and project system. However, Unity generates all `.csproj` files in the root of the main project, which breaks this assumption and prevents Visual Studio from seeing them correctly within submodules.

---

## What does this Unity Editor script do?

This script:

- Uses `AssetPostprocessor.OnGeneratedSlnSolution` and `OnGeneratedCSProject` to hook into Unity's project file generation.
- Creates hard links to Unity-generated `.csproj` files from the directories containing their corresponding `.asmdef` files.
- Rewrites the `.sln` (or `.slnx`) file to use these hard links instead of the originals.
- Ensures paths in the `.csproj` files are shown relative to the submodule project file, not the Unity project root - improving Visual Studio’s Solution Explorer layout.  This means each project's files will be shown relative to its own root, not the Unity project root.

This keeps Unity happy, Git clean, and Visual Studio organised.

---

## Where should I place this script?
This repo contains its own `.asmdef` file and can be placed somewhere under `/Assets`.

> You can use this repo as a Git submodule too!

---

## What about assemblies from packages or the Library folder?

Assemblies that are part of imported Unity packages - such as those found under `Library/PackageCache/` - are ignored. The script only rewrites `.csproj` files generated for source assemblies in your actual Unity project and submodules.

---

## Why use hard links instead of copying files?

Hard links ensure the `.csproj` file appears in the submodule’s directory without duplicating content. Visual Studio sees a project file within the submodule, and Unity continues to manage the original. Since hard links point to the same data, there's no risk of desync or duplication.

---

##  Will this affect other developers?

No.  You shouldn't be tracking `.csproj` or `.sln`\`.slnx` files in Git with Unity anyway as they are automatically generated, so this script will have no effect on anything that is tracked or used by other developers.

---

##  Can this break anything?

This script only affects how solution and project files are generated locally. It does not affect runtime behavior, game logic, or the Unity project itself.

Be sure to test your development and build pipelines after enabling this setup.  Please raise an issue if anything breaks!

---

##  Pro tip

Do not track the `.sln`\`.slnx` and `.csproj` files. Let Unity regenerate them on each developer machine as needed.

---

# Change log
## v2025.2
- Adds support for Visual Studio 2026 (V18) and `.slnx` files.
- Tested with Unity 6.2 (6000.2.14f1)

## v2025.1
- Adds support for Visual Studio 2022 (V15-V17) files

  
