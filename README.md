#  Unity + Git Submodules + VS2022 FAQ

Visual studio 2022 finally supports native Git submodule handling!  Unless you are using Unity to generate your project files.

This repo contains a Unity Editor script to fix project structure issues when using Git submodules with Unity and Visual Studio 2022. It helps ensure that solution and project files are structured in a way that Visual Studio can understand and load properly without affecting Unity or adding clutter to Git.

---

## Why does Visual Studio 2022 require the project files to be inside the submodule?

Visual Studio 2022 requires all referenced `.csproj` files to exist within the same directory tree as the Git submodule for its native Git handling to work. Unity creates all '.csproj` files dynamically in the root of the project, which prevents Visual Studio from seeing them correctly.

---

## What does this Unity Editor script do?

The script ensures that Unity-generated project (`.csproj`) files are created inside the submodule directory instead of the parent repository. It also updates the solution (`.sln`) file.  This makes them visible and usable by Visual Studio 2022.  It does this by creating hard links in the submodule directory to the original Unity-generated `.csproj` files, rather than creating copies.

---

## Where should I place this script?

Place the script in the `Assets/Editor/` directory of your Unity project (within the submodule). Unity automatically compiles scripts in the `Editor` folder in the Editor context.

> You can use this repo as a Git submodule too!

---

##  Why does it use a `.gitunity` file listing the `.csproj` files in each submodule rather than analysing the code or project files directly?

This is due to the way Unity handles extension assemblies (via `asmref`). A single assembly—and its `.csproj` file—can include source files from multiple Git submodules. Visual Studio's Git integration does not handle this situation correctly.

To avoid this, the script uses a manually maintained list of supported `.csproj` files for each submodule. This ensures a clean and accurate solution layout for Visual Studio.

> *Note: This only affects Visual Studio's Git UI behavior. It has no effect on Git itself.*

---

## Does it handle dependencies between submodules?

Not perfectly, but it works. The `.csproj` rewriting does not currently update relative paths between project files in different submodules, and uses the root paths instead.

However, the script uses hard links to replicate the relevant `.csproj` files inside each submodule. Visual Studio then uses these in the solution, and everything resolves correctly in practice.

---

## What does the script modify?

It:

- Forces Unity-generated `.csproj` files to be placed inside the submodule directory tree (as defined by the .gitmodules file).
- Creates and maintains hard links to ensure the submodule contains the necessary project files.
- Rewrites the solution file to point to those linked files.

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

## Format of the `.gitunity` list file

Each `.csproj` file should be listed in a `.gitunity` file n the root of each submodule.  The format should look like this:

```
MySubmodule.Runtime.csproj
MySubmodule.Editor.csproj
```

---
