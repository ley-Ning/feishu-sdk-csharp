#!/bin/bash
# 真机 E2E 验证脚本——凭证到位后一键执行：
#   export FEISHU_APP_ID=cli_xxx
#   export FEISHU_APP_SECRET=xxx
#   export FEISHU_DEMO_RECEIVE_ID=ou_xxx   # 发消息/卡片/流式需要
#   ./run-e2e.sh
set -u
APP_ID="${FEISHU_APP_ID:-}"
APP_SECRET="${FEISHU_APP_SECRET:-}"
RECEIVE_ID="${FEISHU_DEMO_RECEIVE_ID:-}"

if [ -z "$APP_ID" ] || [ -z "$APP_SECRET" ]; then
  echo "✗ 缺少 FEISHU_APP_ID / FEISHU_APP_SECRET"
  echo "  请在 https://open.feishu.cn/app 创建测试应用后 export 凭证"
  exit 1
fi
if [ -z "$RECEIVE_ID" ]; then
  echo "⚠ 缺少 FEISHU_DEMO_RECEIVE_ID：发消息/卡片/流式三项将跳过（查用户/WS 仍可跑）"
fi
echo "=== 真机 E2E：五项验证（sample 已内置四项 + WS 长连接）==="
dotnet run --project samples/FeishuSdk.Sample -c Release
