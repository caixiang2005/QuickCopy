# AGENTS.md

## 项目说明

QuickCopy 是一个 Windows WPF 工具，用于保存常用工作信息、记录剪贴板历史，并将选中的内容快捷粘贴回原来的输入窗口。

项目使用 .NET Framework 4.0、C# 和 WPF，不依赖第三方库。构建脚本是 `build.ps1`，生成文件为 `bin\Release\QuickCopy.exe`。

## Codex 工作约定

- 每次开始工作前，先阅读相关代码和现有文档，理解实际调用链后再修改。
- 遵循 YAGNI，优先使用现有代码、标准库和 Windows 原生能力。
- 保持修改范围最小，不要顺手重构无关代码或引入不必要的依赖。
- 修改代码后运行与改动相关的构建或检查；至少确认项目可以正常生成。
- 不覆盖或撤销用户已有的修改。
- 不要把真实密码、Token、账号或其他敏感信息提交到仓库。
- 生成的 `bin`、`obj` 和本地 exe 文件不提交到 Git，除非用户明确要求。

## 完成后的 Git 操作

当 Codex 判断当前功能已经完成并通过必要检查时：

1. 查看 `git diff` 和 `git status`，确认只包含本次相关修改。
2. 使用英文缩写前缀加中文后缀创建提交信息，例如：

   ```text
   fix: 修复快捷粘贴问题
   feat: 增加剪贴板历史功能
   docs: 更新项目说明
   refactor: 整理记录读取逻辑
   chore: 调整构建脚本
   ```

3. 提交后推送到当前分支对应的远程仓库：

   ```powershell
   git push
   ```

如果本地还没有 Git 仓库、没有配置远程仓库，或远程地址和认证信息不可用，应先完成本地提交，并明确告诉用户需要提供或配置 GitHub 远程地址后才能 push。不要伪造 push 已成功。

## 常用命令

```powershell
git status
git diff
powershell -ExecutionPolicy Bypass -File .\build.ps1
```
