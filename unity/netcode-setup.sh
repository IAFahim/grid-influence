#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C
export LANG=C

root=$(cd -- "$(dirname -- "$0")/.." && pwd)
project=$root/unity/project
target=$project/Packages/com.unity.netcode
version=6.7.0
usage='usage: netcode-setup.sh [install|remove]'

case ${1:-install} in
    install)
        if [ ! -d "$target" ]; then
            work=$(mktemp -d)
            trap 'rm -rf -- "$work"' EXIT
            curl -fsSL -o "$work/netcode.tgz" "https://packages.unity.com/com.unity.netcode/-/com.unity.netcode-$version.tgz"
            tar -xzf "$work/netcode.tgz" -C "$work"
            mv -- "$work/package" "$target"
            sed -i '/class DotsHierarchyItemDecorator/,/^    }$/d' "$target/Editor/Authoring/HierarchyDrawers.cs"
            rm -rf -- "$target/Tests/Editor" "$target/Tests/Editor.meta" "$target/Tests/Runtime/RuntimeGhostAdapter" "$target/Tests/Runtime/RuntimeGhostAdapter.meta"
            printf '[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Gi.Entities.Netcode.Tests")]\n' >>"$target/Tests/Runtime/Utils/AssemblyInfo.cs"
        fi
        printf 'netcode %s embedded at %s\n' "$version" "$target"
        ;;
    remove)
        rm -rf -- "$target"
        git -C "$root" checkout -- unity/project/Packages/packages-lock.json
        printf 'netcode removed\n'
        ;;
    *)
        printf '%s\n' "$usage" >&2
        exit 2
        ;;
esac
