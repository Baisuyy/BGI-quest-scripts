# 插件市场索引

插件页「插件市场」页签读的就是这份 `index.json`。默认地址：

```
https://raw.githubusercontent.com/Baisuyy/BGI-quest-scripts/bgi-special/plugin-market/index.json
```

地址写在 `config.json` 的 `plugin_market_config.index_url`，也可以在插件页的「市场设置」里改，不需要重新发版。

## 格式

```json
{
  "manifest_version": 1,
  "updated_at": "2026-11-26",
  "plugins": [ { ... } ]
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `manifest_version` | 是 | 目前为 `1`。比客户端支持的新时整份索引会被忽略 |
| `updated_at` | 否 | 索引更新时间，显示在市场设置里 |
| `plugins` | 是 | 插件条目数组，可以为空 |

单个插件：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 插件标识，和 `plugin.json` 里的 `id` 一致；已安装判定按它对。只能用小写字母、数字、下划线 |
| `name` | 是 | 显示名称 |
| `version` | 是 | 最新版本，Semver。已装版本更低时显示「更新」 |
| `description` | 否 | 简介，卡片上最多显示两行 |
| `authors` | 否 | 作者列表 |
| `homepage` | 否 | 项目主页 |
| `categories` | 否 | 分类。第一个作为卡片标签，全部进「分类」下拉 |
| `tags` | 否 | 搜索关键词。名称和简介里没写到的别名放这里 |
| `download_url` | 是 | 安装包地址，`https://` 开头，zip 或 nupkg |
| `sha256` | 是 | 安装包的 SHA-256，64 位十六进制。**必填**，校验不过直接拒绝安装 |
| `size` | 否 | 字节数，只用于进度显示 |
| `min_host_version` | 否 | 要求的最低 BetterGI 版本，不满足时卡片显示「不可安装」 |
| `updated_at` | 否 | 这个版本的发布日期 |

缺 `download_url` 或 `sha256` 的条目会被直接丢弃，不会显示成可安装——插件以管理员权限运行，没有校验值的包不能放进来。

## 收录流程

1. 插件按 `Docs/design/plugin-system.md` 第 9.3 节打包成 zip（`plugin.json` 在根目录），或打成 nupkg（`plugin/plugin.json`）。
2. 传到能直链下载的位置（GitHub Release 最省事）。
3. `sha256sum 包名.zip` 取校验值。
4. 在 `plugins` 里加一条，`updated_at` 改成当天。
5. 提交，等 raw 地址刷新（约 1 分钟）。

索引本身不需要签名：每个包都有 `sha256`，安装前逐个校验。但索引地址是明文 https，换源就等于信任那个源——默认地址之外的自建源要自己保证内容。

## 本地联调

```bash
cd /path/to/市场目录      # 里面有 index.json 和安装包
python3 -m http.server 8777
```

插件页「市场设置」里把索引地址改成 `http://127.0.0.1:8777/index.json`，`download_url` 写成 `http://127.0.0.1:8777/xxx.zip` 即可。
