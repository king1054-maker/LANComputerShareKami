# 版本记录

## 0.1.0-alpha.1 — 2026-10-25

首次将完整源码和测试导入 `king1054-maker/LANComputerShareKami`。

- 原生 Windows 双栏文件界面，局域网发现、一次配对、TLS 传输。
- 独立文件复制、目录浏览、删除、改名、回收恢复、同名保留两份。
- 操作去重、文件身份与条件版本检查、校验后提交、崩溃恢复。
- 36 项核心测试及 Windows COM 拖放数据对象自检。
- Windows CI 发现并修复原生改名缓冲区缺少 NUL 结束符的问题；增加不同长度中文名称的重复改名回归测试。
- 增加无弹窗的 `--self-test-drag --headless`，供 Windows 自动测试使用。
- 增加版本索引、AI 接手规则和自动构建/测试/预发布流程。

导入前证据：35 项核心测试在 Linux 通过，Windows 目标编译成功。导入后的 Windows CI 结果以 Actions / release-manifest 为准。

Windows 11 双机人工验收未完成；这不是稳定发行版。
