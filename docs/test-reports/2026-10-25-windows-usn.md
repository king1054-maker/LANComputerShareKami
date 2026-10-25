# Windows 快速编辑版本漏检

失败运行：https://github.com/king1054-maker/LANComputerShareKami/actions/runs/40183469826

原有 `source changed after selection is not silently copied` 测试在 Windows 偶发失败：立即把 `first` 改为等长的 `later` 后，原来的文件身份 + ChangeTime + mtime + size 版本可能完全相同。分支的先前两次通过不能消除这个实证。

修复：Windows 版本额外包含 `FSCTL_READ_FILE_USN_DATA` 返回的 NTFS 文件 USN，不依赖系统时间刻度。原数据句柄保持锁定，使用同身份的同步属性句柄查询，兼容调用者的异步 FileStream。缺少有效日志或不支持的文件系统明确报错，不猜测版本，不自动更改磁盘日志配置。

增加连续 64 次等长编辑并恢复 mtime 的回归测试，无 sleep；保留原失败测试不变。最终结果以对应 Actions / release-manifest 为准。

支持范围因此明确为：Windows 来源和共享文件使用已启用 USN 日志的本机 NTFS 卷。真实 Windows 11 双机验收仍未完成。

官方接口：https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_read_file_usn_data
