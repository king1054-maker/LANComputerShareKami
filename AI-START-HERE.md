# 给接手本项目的 AI：从这里开始

仓库：https://github.com/king1054-maker/LANComputerShareKami

## 拉取哪个版本

- **继续开发**：拉取最新 `main`，记录提交 SHA，再阅读本文件、`AGENTS.md` 与 `VERSION.json`。查看该 SHA 对应的 Actions 状态，不把尚未通过测试的提交当成稳定版。
- **复现已经发布的测试版**：读取 `VERSION.json` 中的 `release_tag` 与 `release_manifest_url`。发布成功后，manifest 给出不可变的源码提交、构建文件和测试证据；检出这个 tag / SHA，不猜版本。
- **目标版本尚未发布**：查看 Actions 失败或执行中的记录；需要旧测试版时到 Releases 选择最近一个已发布版本。`VERSION.json` 是发布意图，不能代替发布成功证据。

```bash
git clone https://github.com/king1054-maker/LANComputerShareKami.git
cd LANComputerShareKami
git fetch origin --tags
git switch main
git pull --ff-only origin main
git rev-parse HEAD
```

不要只读 GitHub 搜索摘要，它可能滞后。优先直接读取仓库文件、提交和发布记录。由于本项目目前发布 alpha 版本，不依赖 GitHub 的 `/releases/latest`；那个入口可能排除预发布版本。

## 固定读取入口

- 机器版本信息：[VERSION.json](VERSION.json)
- 编程规则：[AGENTS.md](AGENTS.md)
- 设计说明：[ARCHITECTURE.md](ARCHITECTURE.md)
- 当前限制和使用方法：[README.md](README.md)
- 变更记录：[CHANGELOG.md](CHANGELOG.md)
- 真实 Windows 11 双机验收：[WINDOWS-ACCEPTANCE.md](WINDOWS-ACCEPTANCE.md)
- 自动测试：[Actions](https://github.com/king1054-maker/LANComputerShareKami/actions)
- 已发布文件：[Releases](https://github.com/king1054-maker/LANComputerShareKami/releases)

原始文件也可直接读取：

- https://raw.githubusercontent.com/king1054-maker/LANComputerShareKami/main/AI-START-HERE.md
- https://raw.githubusercontent.com/king1054-maker/LANComputerShareKami/main/VERSION.json
- https://raw.githubusercontent.com/king1054-maker/LANComputerShareKami/main/AGENTS.md

## 当前工作重点

这是第一次导入的“真实远程目录 + 明确文件操作”实现。不要重新实现后台双向目录镜像。

先看现有 CI 的实际结果。自动 Windows 测试覆盖核心文件句柄行为和 COM 数据对象；它不能证明两台 Windows 11 的局域网、防火墙、资源管理器拖放都已在真人环境中验证。

接下来优先完成双机验收，再处理有明确复现证据的问题。文件级重试已实现，按字节断点续传和边传边播尚未实现。大文件优化必须保持旧请求不覆盖新内容的约束。

## 每次交接最少给出

1. 基于哪个提交、改了哪些具体行为。
2. PR / 新提交链接。
3. 运行了哪些测试及其结果链接。
4. 是否产生新版本、对应 tag 和下载地址。
5. 尚未验证或仍存在的问题。

无需让用户理解 Git 命令；把仓库操作、测试与记录做好，再给用户明确结果。
