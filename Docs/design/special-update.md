# 特别版增量更新

## 为什么做

完整包 546 MB。实测几个 GitHub 代理镜像（gh-proxy、ghproxy.net、ghfast.top、ghfile.geekertao.top），单连接速度在 0.01–1.1 MB/s 之间跳动，还经常 403 或直接连不上。

换镜像解决不了：即便按最好的一档 1 MB/s 算，一次升级也要 9 分钟，且随时可能掉到十分之一。

而一次发版真正变化的通常只有主程序 DLL 与前端产物。special.5 相比 special.4 大了 22 MB，就是补进去的两份前端产物；其余几百 MB 是地图资源和运行时，一个字节都没动。

所以改成：**只下变化的文件**。

## 流水线侧

`build-special.yml` 在打包之后新增 `生成增量包` 步骤：

1. 用 `gh release list` 找上一个 special 版本（本轮 Release 尚未创建，列表第一条即是；同时排除自身 tag，避免重跑任务时取到自己）
2. 下载它的 `special-win-x64.7z` 并解压
3. 与本次 `dist/BetterGI` 逐文件对比：大小不同或 SHA256 不同记为变更，旧版有而新版没有的记为删除
4. 变更文件拷进 `patch/files/`，写 `manifest.json`，打成 `BetterGI_v{新}_patch-from-{旧}.zip`

清单格式：

```json
{
  "from": "0.67.0-special.6",
  "to": "0.67.0-special.7",
  "base": "BetterGI",
  "files": [{ "path": "BetterGenshinImpact.dll", "size": 27418880, "sha256": "…" }],
  "removed": ["Assets/Web/old.html"]
}
```

包体直接从 546 MB 降到几十 MB，前提是两次发版之间没有大资源变动。

该步骤 `continue-on-error`：对比失败只是没有增量包，不影响正常发版。Release 上传时按实际资产数量校验，不写死 3 个。

## 客户端侧

### 判断与下载

`SpecialUpdater` 找到新版本后，先在资产里找 `_patch-from-{当前版本}.zip`：

- 找到 → 弹窗告知增量包大小，问是否现在更新
- 没找到（跨了多个版本、或 atom 兜底拿不到资产名）→ 走原来的完整包链接

下载复用 `Infrastructure/Network/Download`：主地址直连 GitHub，失败按 `Mirrors` 依次换代理，`.part` 保留可续传。新增 `DownloadCategories.SpecialUpdate` 分类，包落在 `update/patch.zip`。

下载期间显示一个临时进度窗（纯代码构建，不进 XAML），显示速度、已下载大小与剩余时间。

### 校验

`SpecialPatch.StageAsync`：

1. 解压到 `update/staging`
2. 读 `manifest.json`，逐文件对 SHA256
3. 任何不一致直接判定失败，退回完整包

清单里的 `files` 允许是数组或单个对象——PowerShell 的 `ConvertTo-Json` 遇到单元素数组会写成对象，只变了一个文件的版本正好会踩到。

路径走 `ResolveStaged` 映射，`..` 越界一律拒绝。

### 应用

运行中的 exe/dll 被系统锁住，进程换不了自己。所以：

1. 写一个临时 PowerShell 脚本
2. 脚本先等所有 `BetterGI` 进程退出（分身也同名，等全部退出才不撞锁）
3. 备份将被覆盖的文件到 `update/backup`
4. 按清单覆盖、删掉 `removed` 里的文件
5. 删 `pending.json`，重新拉起 `BetterGI.exe`
6. 任一步失败 → 回滚备份、把原因写进 `update/last-error.txt`

用户选「稍后重启」时 `pending.json` 保留，下次启动由 `PromptPendingUpdateAsync` 再问一次；若目标版本已等于当前版本，说明文件已经换过，直接清掉标记。

启动时还会读一次 `last-error.txt`，把上次失败原因告诉用户。

## 生效范围

增量更新只在「客户端已经具备这套逻辑」之后才生效。

- special.6 → special.7：special.6 不认识增量包，仍需完整包
- special.7 → special.8 起：可以直接增量

## 验证

沙箱里用脚本跑通了完整闭环：

- 差异检测：改动 1 个、新增 1 个、删除 1 个、未变的资源不入选
- 打包结构：`manifest.json` + `files/…`
- 客户端侧解压、路径校验、SHA256 比对、覆盖加删除
- 结果目录与完整包解压逐文件哈希比对，完全一致

CI 侧 PowerShell 未在沙箱实跑（环境无 pwsh），但逻辑与已验证的 Python 版本一一对应；单元素数组的坑已在客户端做兼容。
