#!/usr/bin/env bash
# ============================================================
# publish-nuget.sh — 打包并发布 NuGet 包
#
# 用法:
#   ./scripts/publish-nuget.sh                  # 使用 csproj 中的版本号
#   ./scripts/publish-nuget.sh 1.0.0            # 覆盖版本号
#   ./scripts/publish-nuget.sh 1.0.0-preview.1  # 预览版
#
# 环境变量:
#   NUGET_CENTER_API_KEY  — NuGet API Key（必须）
#   NUGET_SOURCE          — NuGet 源地址（可选，默认 nuget.org）
# ============================================================

set -euo pipefail

# ── 颜色输出 ──
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

info()  { echo -e "${GREEN}[INFO]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
error() { echo -e "${RED}[ERROR]${NC} $*" >&2; }

# ── 定位项目根目录 ──
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# ── 参数解析 ──
VERSION="${1:-}"
NUGET_SOURCE="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"
CONFIGURATION="Release"
OUTPUT_DIR="$ROOT_DIR/artifacts/nupkg"

# ── 要发布的项目列表 ──
PROJECTS=(
    "src/Mugu.AI.VectorLite/Mugu.AI.VectorLite.csproj"
    "src/Mugu.AI.VectorLite.SemanticKernel/Mugu.AI.VectorLite.SemanticKernel.csproj"
)

# ── 前置检查 ──
if [ -z "${NUGET_CENTER_API_KEY:-}" ]; then
    error "环境变量 NUGET_CENTER_API_KEY 未设置。"
    error "请设置后重试: export NUGET_CENTER_API_KEY=your-api-key"
    exit 1
fi

info "NuGet 源: $NUGET_SOURCE"
info "构建配置: $CONFIGURATION"
if [ -n "$VERSION" ]; then
    info "版本号: $VERSION"
else
    info "版本号: 使用项目文件中的默认版本"
fi

# ── 清理输出目录 ──
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# ── 运行测试 ──
info "正在运行测试..."
cd "$ROOT_DIR"
if ! dotnet test --configuration "$CONFIGURATION" --no-restore --verbosity minimal; then
    error "测试未通过，终止发布。"
    exit 1
fi
info "所有测试通过 ✓"

# ── 打包 ──
info "正在打包..."
for PROJECT in "${PROJECTS[@]}"; do
    PROJECT_PATH="$ROOT_DIR/$PROJECT"
    PROJECT_NAME="$(basename "$PROJECT" .csproj)"

    if [ ! -f "$PROJECT_PATH" ]; then
        error "项目文件不存在: $PROJECT_PATH"
        exit 1
    fi

    PACK_ARGS=(
        --configuration "$CONFIGURATION"
        --output "$OUTPUT_DIR"
        --no-build
    )

    if [ -n "$VERSION" ]; then
        PACK_ARGS+=(-p:Version="$VERSION")
    fi

    info "  打包 $PROJECT_NAME ..."
    dotnet pack "$PROJECT_PATH" "${PACK_ARGS[@]}"
done

# ── 列出生成的包 ──
info "生成的 NuGet 包:"
PACKAGES=("$OUTPUT_DIR"/*.nupkg)
if [ ${#PACKAGES[@]} -eq 0 ]; then
    error "未找到任何 .nupkg 文件。"
    exit 1
fi

for PKG in "${PACKAGES[@]}"; do
    info "  $(basename "$PKG")"
done

# ── 发布确认 ──
echo ""
warn "即将发布以上 ${#PACKAGES[@]} 个包到 $NUGET_SOURCE"
read -r -p "确认发布？[y/N] " CONFIRM
if [[ ! "$CONFIRM" =~ ^[Yy]$ ]]; then
    info "已取消发布。包文件保留在: $OUTPUT_DIR"
    exit 0
fi

# ── 推送到 NuGet ──
info "正在推送到 NuGet..."
PUSH_SUCCESS=0
PUSH_FAIL=0

for PKG in "${PACKAGES[@]}"; do
    PKG_NAME="$(basename "$PKG")"
    info "  推送 $PKG_NAME ..."
    if dotnet nuget push "$PKG" \
        --api-key "$NUGET_CENTER_API_KEY" \
        --source "$NUGET_SOURCE" \
        --skip-duplicate; then
        info "  ✓ $PKG_NAME 推送成功"
        ((PUSH_SUCCESS++))
    else
        error "  ✗ $PKG_NAME 推送失败"
        ((PUSH_FAIL++))
    fi
done

# ── 结果汇总 ──
echo ""
info "════════════════════════════════════════"
info "发布完成: $PUSH_SUCCESS 成功, $PUSH_FAIL 失败"
info "包文件目录: $OUTPUT_DIR"
info "════════════════════════════════════════"

if [ "$PUSH_FAIL" -gt 0 ]; then
    exit 1
fi
