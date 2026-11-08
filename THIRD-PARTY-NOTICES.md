# Third-party components for GlassDesk 0.2.1.3beta

The license inventory was captured on 2026-10-12; package versions, source identifiers and SHA-256 hashes are recorded in [licenses/manifest.json](licenses/manifest.json). Review and refresh it when the locked dependencies change. The license texts govern their respective components; GlassDesk's original source is separately covered by LICENSE.

GlassDesk's layout, icons and placeholder artwork were created for this project. Other workspace projects, MyDockFinder binaries, fonts, pictures, themes and song artwork are not part of the distributable.

## Runtime and installer components

Windows App SDK **binary NuGet packages use Microsoft Software License Terms**, even where an associated source repository uses an open-source license. The table below identifies the actual packages in this release. Some packages are parents of other runtime packages; not every package has a separately named DLL.

| Component | Resolved version | Applicable license and preserved texts |
|---|---|---|
| [Microsoft.WindowsAppSDK](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1) | 2.5.1 | Microsoft Windows App SDK terms; [license](licenses/Microsoft.WindowsAppSDK-2.5.1/license.txt), [third-party notices](licenses/Microsoft.WindowsAppSDK-2.5.1/NOTICE.txt) |
| [Microsoft.WindowsAppSDK.Runtime](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Runtime/2.5.1) | 2.5.1 | Microsoft Windows App SDK terms; [license](licenses/Microsoft.WindowsAppSDK.Runtime-2.5.1/license.txt), [notices](licenses/Microsoft.WindowsAppSDK.Runtime-2.5.1/NOTICE.txt) |
| [Microsoft.WindowsAppSDK.WinUI](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.3.9) | 2.3.9 | Microsoft Windows App SDK terms; [license](licenses/Microsoft.WindowsAppSDK.WinUI-2.3.9/license.txt), [notices](licenses/Microsoft.WindowsAppSDK.WinUI-2.3.9/NOTICE.txt), [XAML tool notices](licenses/Microsoft.WindowsAppSDK.WinUI-2.3.9/tools/NOTICE.txt) |
| [Microsoft.WindowsAppSDK.AI](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.AI/2.5.5) | 2.5.5 | [Microsoft Windows App SDK terms](licenses/Microsoft.WindowsAppSDK.AI-2.5.5/license.txt) |
| [Microsoft.WindowsAppSDK.Base](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Base/2.0.4) | 2.0.4 | [Microsoft Windows App SDK terms](licenses/Microsoft.WindowsAppSDK.Base-2.0.4/license.txt), [notices](licenses/Microsoft.WindowsAppSDK.Base-2.0.4/NOTICE.txt) |
| [Microsoft.WindowsAppSDK.DWrite](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.DWrite/2.1.0) | 2.1.0 | [Microsoft Windows App SDK terms](licenses/Microsoft.WindowsAppSDK.DWrite-2.1.0/license.txt) |
| [Microsoft.WindowsAppSDK.Foundation](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Foundation/2.3.12) | 2.3.12 | [Microsoft Windows App SDK terms](licenses/Microsoft.WindowsAppSDK.Foundation-2.3.12/license.txt) |
| [Microsoft.WindowsAppSDK.InteractiveExperiences](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.InteractiveExperiences/2.1.9) | 2.1.9 | [Microsoft Windows App SDK terms](licenses/Microsoft.WindowsAppSDK.InteractiveExperiences-2.1.9/license.txt) |
| [Microsoft.WindowsAppSDK.ML](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.ML/2.1.94) | 2.1.94 | [Microsoft Windows App SDK / Windows ML terms](licenses/Microsoft.WindowsAppSDK.ML-2.1.94/license.txt) |
| [Microsoft.WindowsAppSDK.Search](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Search/2.5.5) | 2.5.5 | [Microsoft Windows App SDK terms](licenses/Microsoft.WindowsAppSDK.Search-2.5.5/license.txt) |
| [Microsoft.WindowsAppSDK.Widgets](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Widgets/2.0.5) | 2.0.5 | [Microsoft Windows App SDK terms](licenses/Microsoft.WindowsAppSDK.Widgets-2.0.5/license.txt) |
| [Microsoft.Windows.AI.MachineLearning](https://www.nuget.org/packages/Microsoft.Windows.AI.MachineLearning/2.1.74) | 2.1.74 | [Microsoft Windows ML Runtime terms](licenses/Microsoft.Windows.AI.MachineLearning-2.1.74/license.txt), [complete third-party notices](licenses/Microsoft.Windows.AI.MachineLearning-2.1.74/ThirdPartyNotices.txt) |
| [Microsoft.Web.WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77) | 1.0.3719.77 | Microsoft BSD-style license; [license](licenses/Microsoft.Web.WebView2-1.0.3719.77/LICENSE.txt), [notices](licenses/Microsoft.Web.WebView2-1.0.3719.77/NOTICE.txt) |
| [System.Numerics.Tensors](https://www.nuget.org/packages/System.Numerics.Tensors/9.0.0) | 9.0.0 | MIT; [license](licenses/System.Numerics.Tensors-9.0.0/LICENSE.TXT), [third-party notices](licenses/System.Numerics.Tensors-9.0.0/THIRD-PARTY-NOTICES.TXT) |
| [.NET x64 runtime](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.12) | 10.0.12 | MIT; [license](licenses/Microsoft.NETCore.App.Runtime.win-x64-10.0.12/LICENSE.TXT), [third-party notices](licenses/Microsoft.NETCore.App.Runtime.win-x64-10.0.12/THIRD-PARTY-NOTICES.TXT) |
| [Microsoft.Windows.SDK.NET.Ref](https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.26100.57) | 10.0.26100.57 | Windows SDK terms, specified by its [original nuspec](licenses/Microsoft.Windows.SDK.NET.Ref-10.0.26100.57/microsoft.windows.sdk.net.ref.nuspec); [SDK license text](licenses/Microsoft.Windows.SDK.BuildTools.MSIX-1.7.251221100/sdk_license.txt), [installed SDK original RTF](licenses/WindowsSDK-10.0.26100.0/sdk_license.rtf) |
| C#/WinRT `WinRT.Runtime.dll` supplied by the SDK projection pack | File version 2.2.0.48161 | MIT; [original license](licenses/CsWinRT-2.2.0.48161/LICENSE) from the [matching official source commit](https://raw.githubusercontent.com/microsoft/CsWinRT/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d/LICENSE) |
| C++/WinRT generated interop support | Windows SDK 10.0.26100.0 | MIT; [original SDK header license](licenses/CppWinRT-10.0.26100.0/LICENSE.txt) |
| Visual C++ runtime code linked into the native bridge with `/MT` | MSVC 14.51.36231 / Visual Studio 2026 | [Visual C++ runtime terms, original DOCX](licenses/VisualCppRuntime-2026/LICENSE.docx) and [text extraction](licenses/VisualCppRuntime-2026/LICENSE.txt); redistribution rights are governed by the developer's [Visual Studio license](licenses/VisualStudioCommunity-2026/LICENSE.docx) and [official redistribution list](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution) |
| Inno Setup installer/uninstaller runtime | 7.1.1 | [Original Inno Setup license](licenses/InnoSetup-7.1.0/license.txt), verified identical to the 7.1.1 license text; [official license](https://jrsoftware.org/files/is/license.txt) |

The Windows App SDK's transitive Windows ML/ONNX, WebView2 SDK and Tensors binaries appear in the self-contained output. Their presence does not mean GlassDesk offers machine learning or browser features. WebView2 SDK attribution does not assert that a WebView2 browser runtime is bundled. Nested component copyright notices, including those for dependencies inside Microsoft binaries, are retained in the complete supplied `NOTICE` / `THIRD-PARTY-NOTICES` files.

The preserved App SDK and Windows ML notices include Eigen's MPL 2.0 text and Microsoft's source-code availability information, including the `3rdpartysource.microsoft.com` portal and the original postal request instructions. Retain those complete notices together with the binaries; they identify the source-access and component-specific rights separately from Microsoft's binary license.

`Microsoft.Windows.SDK.NET.Ref` is an implicit SDK dependency and is therefore also checked in the generated `GlassDesk.deps.json`; it is not a direct `PackageReference`. C#/WinRT's version above is an actual DLL file version, rather than an invented NuGet package version.

## Visual study references for 0.3.0-preview.1

The independent companion contour/motion, Windows GPU material, and transport transitions were visually studied against [grok-icon-study](https://github.com/blessonism/grok-icon-study), [GlassExplorer](https://github.com/ktiays/GlassExplorer), and [milegelu's Uiverse interaction](https://uiverse.io/milegelu/grumpy-wombat-18). No reference assets, source files, Apple private APIs, or Uiverse CSS are bundled. The Uiverse example supplies short hover/press and icon-change inspiration; the direction-aware album and metadata transition is a separate Composition implementation. These references are not runtime dependencies.

## Liquid glass optics (Glasswork)

The liquid glass plugin's rounded-rectangle lens refraction, separable Gaussian blur, material tint and rim layers (D3D11 shaders in `native/GlassDesk.Native`) and the WebGL2 showcase page `docs/index.html` are ported from and inspired by [Glasswork](https://github.com/kubai087/glasswork) by Kubai087, under the MIT License. The [original license](licenses/Glasswork/LICENSE) (Copyright (c) 2026 Kubai087, from commit `4f0b6744b898b3a9b98fbd913ab24fdb83c53162`) is preserved. Glasswork's source files, assets and dependencies are not bundled; GlassDesk captures the desktop on the GPU only.

## Build-only components

| Component | Version used | License / original source |
|---|---|---|
| .NET SDK | 10.0.401 | Pinned in `global.json`; the runtime redistributed by this release is listed separately above |
| Windows SDK native headers and tools | 10.0.26100.0 | [Original installed SDK license](licenses/WindowsSDK-10.0.26100.0/sdk_license.rtf); Windows OS API DLLs themselves are supplied by Windows |
| [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/10.0.26100.4654) | 10.0.26100.4654 | [Original nuspec with SDK license URL](licenses/Microsoft.Windows.SDK.BuildTools-10.0.26100.4654/microsoft.windows.sdk.buildtools.nuspec); [Microsoft SDK license endpoint](https://aka.ms/WinSDKLicenseURL) |
| [Microsoft.Windows.SDK.BuildTools.MSIX](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools.MSIX/1.7.251221100) | 1.7.251221100 | [Original SDK license](licenses/Microsoft.Windows.SDK.BuildTools.MSIX-1.7.251221100/sdk_license.txt), [notices](licenses/Microsoft.Windows.SDK.BuildTools.MSIX-1.7.251221100/NOTICE.txt) |
| Visual Studio Community 2026 / MSVC x64 | MSVC 14.51.36231 | [Original Microsoft license DOCX](licenses/VisualStudioCommunity-2026/LICENSE.docx), [text extraction](licenses/VisualStudioCommunity-2026/LICENSE.txt), [official license page](https://visualstudio.microsoft.com/license-terms/vs2026-ga-community/) |
| Inno Setup compiler | 7.1.1 x64 | Same Inno license above (the 7.1.1 license text matches the retained 7.1.0 text); the compiler itself is not installed with GlassDesk |

Build tool binaries are excluded from the application payload. The SDK terms and compiler terms remain in this source/notice bundle for provenance. Eligibility to use Visual Studio Community is governed by its own individual and organizational rules; this bundle does not extend those rights. Visual C++ runtime end-user terms do not, by themselves, grant a developer redistribution permission.

## Retaining notices when distributing

The previous/next music control contours in `src/GlassDesk.App/SkipGlyphVisual.cs` are adapted from [MoFeng2223/BrimDeck's MusicVisuals.cs](https://github.com/MoFeng2223/BrimDeck/blob/e3765b1b51c5f3fb7d05feee7c1f6f4ba082eff3/src/BrimDeck/MusicVisuals.cs), under Apache-2.0. The [original license](licenses/BrimDeck/LICENSE) and [scope/change notice](licenses/BrimDeck/NOTICE.md) are preserved. This ports only vector contours to WUC; BrimDeck's WPF application, dependencies and NetEase debugging bridge are not included.

The Home navigation geometry and folder opening interaction are adapted to Windows.UI.Composition with attribution to [Smit-Prajapati's massive-insect-5](https://uiverse.io/Smit-Prajapati/massive-insect-5) and [Cobp's mighty-pig-13](https://uiverse.io/Cobp/mighty-pig-13). Their MIT notices are retained in [licenses/Uiverse-home-components/LICENSE.txt](licenses/Uiverse-home-components/LICENSE.txt). No HTML/CSS runtime or third-party image assets are bundled; application icons are read from installed local applications at runtime, with original fallback glyphs when unavailable.

Distribute this file and the complete `licenses` directory together with the unmodified self-contained application files. The installed files include `licenses/manifest.json` and the original licenses/notices. The installer shows [component license terms](licenses/installer-third-party-terms.txt), assembled from preserved applicable license texts. The separate original documents remain authoritative; the combined presentation deduplicates identical texts and adds a short scope explanation.

The Microsoft Windows App SDK and Windows ML binary terms require significant application functionality and terms protecting the redistributable components for external users and distributors (see their Distribution Requirements sections). Preserve original notices and satisfy the component-specific conditions when repackaging. MIT, BSD-style and nested third-party licenses retain their own grants and requirements. No Microsoft endorsement is implied.

`licenses/collect_licenses.py` recreates the bundle from the exact restored package versions and installed SDK/Inno files, with fixed official source URLs for licenses absent from those local packages. The DOCX sources are retained unchanged alongside clearly marked plain-text extractions. Package versions and source hashes are recorded in [the manifest](licenses/manifest.json); rebuild this inventory when dependencies change.
