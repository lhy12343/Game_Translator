#!/usr/bin/env bash
# 发布 GameTranslator 正式版、Debug 版和可选择安装目录的安装器
# 用法: bash tools/publish.sh [目标目录, 默认 /mnt/c/Users/QingFeng/Desktop/GameTranslator]
set -euo pipefail
cd "$(dirname "$0")/.."

DEFAULT_TARGET=/mnt/c/Users/QingFeng/Desktop/GameTranslator
TARGET="${1:-$DEFAULT_TARGET}"
RID=win-x64
BUILD_TMP="$(mktemp -d)"
trap 'rm -rf "$BUILD_TMP"' EXIT

# 每次发布都由源码重建嵌入式批量端点，避免 DLL 与源码漂移。
XUNITY_ZIP="$BUILD_TMP/xunity.zip"
curl -fsSL "https://github.com/bbepis/XUnity.AutoTranslator/releases/download/v5.6.1/XUnity.AutoTranslator-BepInEx-5.6.1.zip" -o "$XUNITY_ZIP"
echo "FBB7D1BBE2C7CC168DA6DCCBC500FB74786A85A548F52495C8A1592AC46407F5  $XUNITY_ZIP" | sha256sum -c -
7z x -y -bd -o"$BUILD_TMP/xunity" "$XUNITY_ZIP" >/dev/null
dotnet build src/xunity-batch -c Release -p:XUnityCorePath="$BUILD_TMP/xunity/BepInEx/plugins/XUnity.AutoTranslator/XUnity.AutoTranslator.Plugin.Core.dll"
cp src/xunity-batch/bin/Release/net35/CustomTranslate.dll src/xunity-batch/CustomTranslate.dll

if [[ "$TARGET" == "$DEFAULT_TARGET" ]]; then
    # 默认目录的程序可能正在运行，先解除文件占用。
    TASKKILL=/mnt/c/Windows/System32/taskkill.exe
    "$TASKKILL" /F /IM GameTranslator.exe 2>/dev/null || true
    "$TASKKILL" /F /IM GameTranslatorDebug.exe 2>/dev/null || true
    sleep 1
fi

APP_TMP="$BUILD_TMP/app"
mkdir -p "$APP_TMP" "$TARGET"
rm -f "$TARGET"/GameTranslatorDebug.*
dotnet publish src/gui -c Release -r $RID --self-contained true -o "$APP_TMP"
rm -rf "$TARGET"/*
cp -a "$APP_TMP"/. "$TARGET"/
dotnet publish src/gui -c Debug -r $RID --self-contained true -o "$TARGET"

PAYLOAD="$BUILD_TMP/GameTranslator-payload.zip"
(cd "$APP_TMP" && 7z a -tzip -mx=9 "$PAYLOAD" . >/dev/null)
dotnet publish src/installer/Installer.csproj -c Release -r $RID --self-contained true \
    -p:PayloadPath="$PAYLOAD" -o "$TARGET"

echo "已打包到 $TARGET:"
ls "$TARGET"/GameTranslator.exe
ls "$TARGET"/GameTranslatorDebug.exe
ls "$TARGET"/GameTranslator-Setup.exe
