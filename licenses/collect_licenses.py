"""Rebuild this license bundle from official installed packages and fixed primary URLs.

Only GlassDesk/licenses is written. No application, registry, startup or Windows settings
are changed. Original file bytes and SHA-256 provenance are retained.
"""
import hashlib
import json
import re
import shutil
import urllib.request
import zipfile
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]
DESTINATION = ROOT / "licenses"
NUGET = Path.home() / ".nuget" / "packages"
locked = json.loads((ROOT / "src/GlassDesk.App/packages.lock.json").read_text(encoding="utf-8"))
dependencies = locked["dependencies"]["net10.0-windows10.0.26100"]
packages = [(name, item["resolved"]) for name, item in dependencies.items() if item["type"] != "Project"]
packages.extend([("Microsoft.NETCore.App.Runtime.win-x64", "10.0.12"), ("Microsoft.Windows.SDK.NET.Ref", "10.0.26100.57")])
manifest = {"schemaVersion": 1, "appVersion": "0.2.1.3-beta", "capturedDate": "2026-10-12", "packages": [], "files": []}
runtime_license_paths = []
build_only = {"Microsoft.Windows.SDK.BuildTools", "Microsoft.Windows.SDK.BuildTools.MSIX"}


def remember(path, source, component, version, kind="original"):
    manifest["files"].append({"path": path.relative_to(ROOT).as_posix(), "component": component,
                              "version": version, "source": source, "kind": kind,
                              "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})


def copy_original(source, destination, origin, component, version):
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)
    remember(destination, origin, component, version)


for name, version in packages:
    source = NUGET / name.lower() / version
    if not source.is_dir():
        raise FileNotFoundError(f"Official restored package is absent: {name}/{version}")
    target = DESTINATION / f"{name}-{version}"
    manifest["packages"].append({"id": name, "version": version, "packageUrl": f"https://www.nuget.org/packages/{name}/{version}",
                                  "scope": "build-only" if name in build_only else "runtime-or-runtime-parent"})
    for path in source.rglob("*"):
        if not path.is_file():
            continue
        is_license = re.search(r"(?i)(license|notice|third.?party|copyright)", path.name) and path.suffix.lower() in (".txt", ".md", ".rtf", ".html", "")
        is_nuspec = path.parent == source and path.suffix.lower() == ".nuspec"
        if not is_license and not is_nuspec:
            continue
        relative = path.relative_to(source)
        output = target / relative
        copy_original(path, output, f"NuGet:{name}/{version}/{relative.as_posix()}", name, version)
        if name not in build_only and re.match(r"(?i)^license\.(txt|md)$", path.name):
            runtime_license_paths.append(output)

sdk_root = Path(r"C:\Program Files (x86)\Windows Kits\10")
copy_original(sdk_root / "Licenses/10.0.26100.0/sdk_license.rtf", DESTINATION / "WindowsSDK-10.0.26100.0/sdk_license.rtf",
              "Installed Windows SDK 10.0.26100.0:Licenses/sdk_license.rtf", "WindowsSDK", "10.0.26100.0")
cpp_output = DESTINATION / "CppWinRT-10.0.26100.0/LICENSE.txt"
copy_original(sdk_root / "Include/10.0.26100.0/cppwinrt/LICENSE.txt", cpp_output,
              "Installed Windows SDK 10.0.26100.0:Include/cppwinrt/LICENSE.txt", "C++/WinRT", "WindowsSDK-10.0.26100.0")
runtime_license_paths.append(cpp_output)
inno_output = DESTINATION / "InnoSetup-7.1.0/license.txt"
copy_original(ROOT / ".tools/innosetup-7.1.0/license.txt", inno_output,
              "Official Inno Setup 7.1.0 portable distribution:license.txt", "Inno Setup", "7.1.0")
runtime_license_paths.append(inno_output)

downloads = [
    ("CsWinRT", "2.2.0.48161", "CsWinRT-2.2.0.48161/LICENSE", "https://raw.githubusercontent.com/microsoft/CsWinRT/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d/LICENSE"),
    ("Visual C++ Runtime", "2026-v14", "VisualCppRuntime-2026/LICENSE.docx", "https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual-C-V14-License-Redistributable_and_Runtime_ENU.docx"),
    ("Visual Studio Community", "2026", "VisualStudioCommunity-2026/LICENSE.docx", "https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual_Studio_2026-License-Community_ENU.docx"),
]
for component, version, relative, url in downloads:
    output = DESTINATION / relative
    output.parent.mkdir(parents=True, exist_ok=True)
    if not output.exists():
        request = urllib.request.Request(url, headers={"User-Agent": "GlassDesk-LicenseInventory/0.1"})
        with urllib.request.urlopen(request, timeout=30) as response:
            contents = response.read(2 * 1024 * 1024)
        output.write_bytes(contents)
    remember(output, url, component, version)
    if output.suffix == ".docx":
        with zipfile.ZipFile(output) as archive:
            document = ElementTree.fromstring(archive.read("word/document.xml"))
            namespaces = {"w": "http://schemas.openxmlformats.org/wordprocessingml/2006/main"}
            paragraphs = ["".join(node.text or "" for node in paragraph.findall(".//w:t", namespaces))
                          for paragraph in document.findall(".//w:body//w:p", namespaces)]
        text_path = output.with_suffix(".txt")
        text_path.write_text("\n\n".join(paragraphs) + "\n", encoding="utf-8")
        remember(text_path, url, component, version, "plain-text extraction; original DOCX preserved")
        if component == "Visual C++ Runtime":
            runtime_license_paths.append(text_path)
    else:
        runtime_license_paths.append(output)

# The Windows SDK projection package points to the SDK terms but carries no text.
# Preserve its nuspec and installed RTF, plus the SDK text bundled by the official MSIX build-tools package.
runtime_license_paths.append(DESTINATION / "Microsoft.Windows.SDK.BuildTools.MSIX-1.7.251221100/sdk_license.txt")
glasswork_license = DESTINATION / "Glasswork/LICENSE"
remember(glasswork_license,
         "https://raw.githubusercontent.com/kubai087/glasswork/4f0b6744b898b3a9b98fbd913ab24fdb83c53162/LICENSE",
         "Glasswork", "4f0b6744b898b3a9b98fbd913ab24fdb83c53162")
runtime_license_paths.append(glasswork_license)
seen = set()
terms = ["GlassDesk 0.2.1.3 beta - 随附第三方组件许可条款 / Bundled third-party component terms\n\n"
         "安装即表示同意遵守以下条款中适用于随附第三方组件的规定。完整第三方声明保存在安装目录的 licenses 文件夹。"
         "本文件不授予或更改 GlassDesk 自身源代码的许可，也不改变开源组件许可授予的权利。\n\n"
         "By installing, you agree to the following terms as applicable to the bundled third-party components. "
         "Complete notices are retained in the installed licenses directory. This file does not grant or change "
         "a license to GlassDesk's own source and does not restrict rights granted by the open-source component licenses.\n"]
for path in runtime_license_paths:
    contents = path.read_bytes()
    digest = hashlib.sha256(contents).hexdigest()
    if digest in seen:
        continue
    seen.add(digest)
    terms.append("\n\n" + "=" * 72 + "\n" + path.relative_to(ROOT).as_posix() + "\n" + "=" * 72 + "\n\n" + contents.decode("utf-8-sig"))
terms_file = DESTINATION / "installer-third-party-terms.txt"
terms_file.write_text("\n".join(terms), encoding="utf-8-sig")
remember(terms_file, "Concatenation of preserved applicable licenses listed in this file", "Installer component license acceptance", "0.2.1.3-beta", "combined original texts with explanatory introduction")
(DESTINATION / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"packages": len(manifest["packages"]), "files": len(manifest["files"]), "installerTermsBytes": terms_file.stat().st_size,
                  "manifest": "licenses/manifest.json"}, indent=2))
