# Windows 原生改名故障与修复

首次 Windows CI 真实执行发现改名或上传后的实际名称偶发不符：

- 初始运行：https://github.com/king1054-maker/LANComputerShareKami/actions/runs/40182908882
- 诊断运行：https://github.com/king1054-maker/LANComputerShareKami/actions/runs/40183100816
- 后一轮 `windows-directory-diagnostics.txt` 明确显示文件实际名为 `movie.bin翾`，而请求目标为 `movie.bin`。

`FILE_RENAME_INFO` 的 FileNameLength 不包含结束符，但 Win32 路径字符串仍需要终止 WCHAR。原实现只分配了有效字节，末尾的未初始化堆内存可能被算进目标路径。修复为额外分配并写入一个零 WCHAR，保留正确 FileNameLength。

回归覆盖：48 次不同长度、含中文的精确文件名移动，以及目录移动；检查目标确切存在、旧路径不存在和内容不变。保留原有全部测试断言。最终结果以修复提交对应 Actions 和发布 manifest 为准。

微软接口文档：https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info

这项修复不代表 Windows 11 实体双机验收通过。
