# 测试证据位置

- `tests/test-results.txt`：首次源码包的 Linux 测试历史记录，35 项通过。
- `tests/windows-build.txt` / `tests/windows-publish.txt`：首次源码包的 Windows 目标编译记录；不是 Windows 实机运行证据。
- GitHub Actions：每个新提交对应的最新真实运行结果。失败记录仍保留，不能用旧日志覆盖失败状态。
- 每次成功发布的 Release：附带 `linux-core-tests.txt`、`windows-core-tests.txt`、`windows-drag-self-test.txt` 和 `release-manifest.json`，关联确切提交与 workflow run。
- Windows 11 两台实体电脑手工结果：按 `WINDOWS-ACCEPTANCE.md` 执行后另存带日期的报告，写明设备/系统、步骤、结果和失败复现。未执行时保持 `not_run`。
