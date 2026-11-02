# Body Detailer

A Windows tool for Skyrim Special Edition that generates procedural skin textures for bodies, heads and hands/feet, with a live 3D preview, and can patch them onto individual NPCs.

## What it does

- **Skin**: procedural skin layers (pores, wrinkles, veins, freckles, body hair, fur and more), baked into diffuse, model-space normal, specular and subsurface maps, with BC7/BC1/BC3 compression per map and separate export sizes for body, head and hands/feet. A fur shell texture can be exported for Bottled/Open Shaders fur shells.
- **Regions**: masked areas with their own skin settings, blended over the base.
- **Neck seam fix**: blends the head and body textures across the neck seam.
- **Genitals**: bakes a TNG genital add-on (such as TRX) so its skin continues the NPC's body texture, grows a pubic hair card mesh around it, builds it with your BodySlide preset (no BodySlide run needed), and gives it to the NPC as one slot-52 piece through a patch plugin.
- **Load order patcher**: gives NPCs from your load order their generated textures.
- **Normal sliders**: BodySlide sliders that reshape only the normal bake.

## Requirements

- Windows, [.NET 10 desktop runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Skyrim Special Edition, run through Mod Organizer 2
- A GPU with OpenGL 4.x for the preview and bake

## Usage

1. Extract the release anywhere (for example `MO2\tools\Body Detailer`), add `SkinTextureTool.exe` to MO2's executables and launch it from there, so it sees your mods.
2. Load an NPC, tweak the layers, and export, or use the Genitals and Load order patcher tabs for per-NPC patches.
3. With MO2, move the output from overwrite (or the tool's output folder) into a mod and enable it after your NPC appearance patches.

Genital blend masks go in the `Genital masks` folder next to the exe; one whose name matches the add-on (for example `TRX.png`) is picked automatically.

### Getting the TRX mask

The mask for TRX comes from m4mk203's TRX .blend, available on [Patreon](https://www.patreon.com/m4mk203/posts/trx-futa-addon-56012453):

1. Download the .blend from that post and open it in Blender.
2. Switch an area to the **Image Editor** and pick the image **Shlong_Mask10** from the image dropdown.
3. **Image > Save As**, choose PNG, and save it as `TRX.png` in the `Genital masks` folder next to `SkinTextureTool.exe` (create the folder if it isn't there).
4. In the Genitals tab, pick the TRX add-on; the mask fills in on its own.

## Building

```
dotnet build
dotnet publish src/SkinTextureTool/SkinTextureTool.csproj -c Release -r win-x64 --self-contained false -o out
```

The exe opts out of CET shadow stacks (`<CETCompat>false</CETCompat>`); without that it crashes on start under MO2's virtual file system.

## Built with

- [Mutagen](https://github.com/Mutagen-Modding/Mutagen) for plugins and archives
- [NiflySharp](https://github.com/ousnius/nifly) (nifly) for meshes
- [BodySlide and Outfit Studio](https://github.com/ousnius/BodySlide-and-Outfit-Studio) (GPL-3.0) as the reference for slider sets, morph files and building
- [BCnEncoder.Net](https://github.com/Nominom/BCnEncoder.NET) and [Pfim](https://github.com/nickbabcock/Pfim) for textures
- [OpenTK](https://github.com/opentk/opentk) for the preview

## License

GPL-3.0. See [LICENSE](LICENSE).
