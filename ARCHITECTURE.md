# 开发者接手说明

## 不得改变的语义

一个目录只有一个权威所有者：保存它的那台电脑。UI 目录刷新是只读请求；复制、删除、改名是明确命令。不存在后台镜像、跨机 mtime 比较、删除传播或 409 反向覆盖。

## 项目结构

| 文件 | 职责 |
|---|---|
| Core/Identity.cs | 每设备证书、指纹配对、持久化信任、事件日志 |
| Core/Network.cs | IPv4 发现、TLS 双向证书、命令服务与客户端 |
| Core/Wire.cs | 4 字节大端长度 + JSON；文件主体按确切长度传输；分块 SHA-256 |
| Core/FileSystem.cs | Windows 文件身份、ChangeTime、句柄重命名、路径联接防护；Linux 测试后端 |
| Core/Store.cs | 共享目录边界、条件操作、临时接收、回收区、操作日志及恢复 |
| Core/Transfers.cs | 清单、两个并发任务、进度、暂停、持久化重试、目录递归 |
| Windows/FilePane.cs | 真实目录显示、拖入目标、导航、选择、过期 UI 响应保护 |
| Windows/Dialogs.cs | 延迟 FileDrop 的 managed IDataObject / ITypedDataObject 适配器 |
| Windows/DragInteropTest.cs | Windows COM 拖放自检入口 |
| Windows/MainForm.cs | 设备选择、配对、原生窗口、传输列表和回收恢复 |

## 请求协议

每次请求一个 TLS 连接。双方必须证明私钥持有；客户端固定服务器 SHA-256 证书指纹。服务器只允许未授权证书调用 `pair`，其余调用要求已授权身份。自动发现消息只是地址线索，不是授权。

`list` 返回条目、文件身份和版本，以及目录本身的稳定身份。

`upload` 包含 operationId、目标目录、目标目录身份、文件名、长度和 SHA-256。服务器先查历史操作：已完成则直接返回结果；新操作先返回 ready，再接收恰好指定长度的主体。校验后重新验证目标目录身份与信任状态，然后提交。不同请求重用同一 operationId 会拒绝。

`download` 包含源路径和预期版本。服务器打开文件并禁止其他写入/删除、核对版本、计算 SHA-256 后发送元信息和主体。客户端校验成功才提供本地完整文件。

`delete`、`rename` 包含文件路径与预期版本；`mkdir` 带父目录身份。文件版本是单个所有者上的变化标识，不是全局排序号。

## Windows 文件身份与竞态

文件身份由卷标识、文件索引、创建时间组成；Windows 版本另外包含 NTFS 文件 USN、ChangeTime、LastWriteTime 和大小。快速写入可能落在同一个系统时间刻度，因此不能仅依靠 ChangeTime。通过 FSCTL_READ_FILE_USN_DATA 读取文件 USN，以识别同大小、恢复 mtime 的修改。USN 在本机用于等值检查，不跨机器排序。读取不到有效 USN 时明确拒绝操作，不降级为时间戳猜测。此测试版的 Windows 来源及共享目录要求本机 NTFS 卷已启用 USN 日志。

操作期间锁住祖先目录，拒绝 reparse point，阻止祖先被替换。对破坏性文件操作使用同一个带 DELETE 权限的句柄核对版本并重命名；文件内容读写使用 Windows 共享模式保护。目录子项不构成整体快照。Windows 本机恶意管理员、内核或底层磁盘修改不在应用级防护范围内。

回收删除是同卷移动，不是遍历后永久删除。目录包含的文件被保留在回收区。

## 文件系统与日志不是一个事务

1. 原子写出 prepared 操作记录，包含确定目标和源文件身份。
2. 使用不覆盖的同卷重命名提交。
3. 持久化 done 记录，然后发送成功回执。

崩溃恢复只在目标仍具有记录中的身份时确认完成。没有充分证据就标记 ambiguous，并阻止同一个操作再次执行。该策略宁可要求人工核对，也不猜测并重做破坏性操作。

它覆盖进程崩溃与丢失回执场景，**不承诺对任意硬件断电、文件系统损坏具有数据库级事务保证**。没有悄悄启用自动覆盖。

上传重试 ID 由任务 ID + 清单相对路径确定。目录创建的返回路径（可能因同名而改名）持久化到任务，子项使用这个实际路径。更换来源文件内容后应创建新任务，不伪装成旧任务重试。

## UI 与 Windows 拖放

内部拖放交换自定义 JSON：源设备和源条目。外部 FileDrop 则交换真实本地路径。远程拖出在资源管理器索取 FileDrop 时下载缓存，不把 URL 或远程路径冒充本地文件。

.NET 10 的 DataObject 原生 COM 实现转发到内部 composition；仅继承 DataObject 并覆写 GetData 不足以保证延迟获取。当前实现使用独立 managed IDataObject / ITypedDataObject，再包装为 DataObject。

由于完整远程文件先缓存，Explorer 可能等待；后续产品化可以替换成 FILEDESCRIPTOR / FILECONTENTS + IStream 的虚拟文件流，但不得把未经测试的延迟流声称为已实现。

## 下载器接入示例

```csharp
// Node 和 TransferQueue 通常在应用启动时创建并保持整个进程生命周期。
var peer = node.GetPeer(targetDeviceId);
var directory = await client.CallAsync(peer, new Request { Kind = "list", Path = targetFolder });
queue.Enqueue("", downloadedFilePath, null, peer.Id, targetFolder, directory.Version);
```

订阅 `queue.Progress` 与 `queue.Changed` 更新 UI；UI 线程通过 BeginInvoke 切回。首次配对由 `node.PairApproval` 提供明确用户确认。不要直接在下载完成事件里把全部文件广播给全部设备。

## 后续优先级

1. 先按 Windows 验收清单完成两台真机测试，修复可重复失败，不先扩展架构。
2. 大文件按块续传、直连下载到目标暂存，减少本机中转缓存。
3. 真正的异步 Shell 虚拟文件拖出，以及流式播放。
4. 精细权限与回收区容量策略；更好的任务历史与已完成任务清理。
5. 如确需公网连接，再单独设计网络发现/中继，不混入目录镜像。

官方技术参考：

- https://learn.microsoft.com/dotnet/core/extensions/sslstream-troubleshooting
- https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandleex
- https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
- https://source.dot.net/Microsoft.Private.Windows.Core/System/Private/Windows/Ole/Composition.ManagedToNativeAdapter.cs.html
- https://learn.microsoft.com/dotnet/desktop/winforms/advanced/how-to-retrieve-data-from-the-clipboard
