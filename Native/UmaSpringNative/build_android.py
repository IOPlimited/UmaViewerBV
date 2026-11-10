"""Build the ARM64 Android CySpring plugin with an Android NDK toolchain."""
import argparse
import os
from pathlib import Path
import platform
import struct
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ndk', default=os.environ.get('ANDROID_NDK_HOME') or os.environ.get('ANDROID_NDK_ROOT'))
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if not args.ndk:
        sdk = os.environ.get('ANDROID_SDK_ROOT') or os.environ.get('ANDROID_HOME')
        installed = list((Path(sdk) / 'ndk').glob('*')) if sdk else []
        if installed:
            args.ndk = max(installed, key=lambda path: tuple(int(part) for part in path.name.split('.')))
    if not args.ndk:
        parser.error('Pass --ndk with the Unity AndroidPlayer/NDK directory.')
    ndk = Path(args.ndk)
    host = {'Windows': 'windows-x86_64', 'Linux': 'linux-x86_64', 'Darwin': 'darwin-x86_64'}[platform.system()]
    toolchain = ndk / 'toolchains/llvm/prebuilt' / host
    compiler = toolchain / 'bin' / ('clang++.exe' if os.name == 'nt' else 'clang++')
    root = Path(__file__).resolve().parents[2]
    output = args.output or root / 'Assets/Plugins/Android/libs/arm64-v8a/libUmaSpringNative.so'
    output.parent.mkdir(parents=True, exist_ok=True)
    command = [str(compiler), '--target=aarch64-linux-android21', '--sysroot=' + str(toolchain / 'sysroot'),
               '-std=c++17', '-O3', '-shared', '-fPIC', '-fvisibility=hidden', '-fno-exceptions', '-fno-rtti',
               '-fno-fast-math', '-ffp-contract=off', '-fno-strict-aliasing', '-fno-math-errno',
               str(Path(__file__).resolve().parent / 'src/kernel.cpp'), '-static-libstdc++', '-lm',
               '-Wl,--no-undefined', '-Wl,-z,max-page-size=16384', '-Wl,-soname,libUmaSpringNative.so',
               '-o', str(output)]
    subprocess.run(command, check=True)
    header = output.read_bytes()[:20]
    if header[:6] != b'\x7fELF\x02\x01' or struct.unpack_from('<H', header, 18)[0] != 183:
        raise RuntimeError('Expected a little-endian AArch64 ELF64 shared library')
    print('Built Android ARM64 CySpring: ' + str(output))


if __name__ == '__main__':
    main()
