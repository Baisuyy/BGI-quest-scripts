# 路线任务（JSON）开发

路线任务是「自动采集 / 挖矿 / 锄地 / 自动首领」用的数据文件：一串地图坐标 + 每个点做什么。
它不是代码，没有循环和变量，靠识别小地图把角色带到每个点。适合**点位固定、流程线性**的场景。

想写带判断逻辑的东西，看 [script-js.md](script-js.md)；想让 BGI 有新能力，看 [plugin-dev.md](plugin-dev.md)。

---

## 1. 文件放在哪

```
<User>/AutoPathing/<分组>/<任务名>.json
```

`<User>` 是 BGI 的用户目录（便携版就是软件目录下的 `User`）。
**子目录就是界面上的分组**，可以嵌套。同一目录下的多个任务按 `info.order` 排序依次执行（一条龙、循环刷时用得上）。

界面入口：左侧「地图追踪」。新建一个 → 在里面点「录制」走一遍，就会生成草稿，比手写坐标省事得多。

---

## 2. 最小可用示例

```json
{
  "info": {
    "name": "晨曦酒庄 蒲公英",
    "type": "collect",
    "version": "1.0",
    "bgi_version": "0.67.0",
    "map_name": "Teyvat",
    "map_match_method": "TemplateMatch",
    "tags": ["蒲公英", "蒙德"]
  },
  "positions": [
    { "id": 1, "type": "teleport", "x": 7164.0811, "y": 9309.9551, "move_mode": "walk" },
    { "id": 2, "type": "path", "x": 7165.3457, "y": 9326.8311, "move_mode": "run" },
    { "id": 3, "type": "target", "x": 7213.6914, "y": 9415.1230, "move_mode": "walk", "action": "pick_up_collect" }
  ]
}
```

保存成 `User/AutoPathing/采集/晨曦酒庄 蒲公英.json`，刷新界面就能看到。点开始，角色会先传送到第 1 个点，再依次走过去。

---

## 3. 顶层结构

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `info` | object | 元信息，见下 |
| `positions` | array | 路径点数组，按顺序执行 |
| `config` | object | 可选。`{ "realtime_triggers": { "AutoPick": true } }`，覆盖本次运行的实时触发器开关 |

---

## 4. `info` 字段

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `name` | 是 | 任务名，界面上显示的就是它 |
| `type` | 是 | `collect` 采集 / `mining` 挖矿 / `farming` 锄地。决定界面分类和默认拾取策略 |
| `version` | 否 | 自己维护的版本号，字符串即可 |
| `bgi_version` | 否 | 制作时的 BGI 版本。**当前版本低于它时会拒绝运行并提示升级**，别随手写高 |
| `description` | 否 | 说明，界面上最多显示 6 行 |
| `author` | 否 | 作者，字符串 |
| `tags` | 否 | 标签数组，界面筛选用 |
| `order` | 否 | 同目录内的执行顺序，默认 0 |
| `map_name` | 否 | 地图，默认 `Teyvat`。分层地图（渊下宫、层岩巨渊地下等）要写对应的值 |
| `map_match_method` | 否 | `TemplateMatch`（推荐，支持分层地图）或 `SIFT`（老方式） |
| `enable_monster_loot_split` | 否 | 锄地时按怪物类型分开展示掉落 |
| `items` | 否 | 采集物/怪物清单，用于统计，可留空 |

> 老文件里可能写 `authors: [{name, links}]`，那是旧格式，现在只读 `author` 字符串。

---

## 5. 路径点 `positions`

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 否 | 序号，只用于显示与排查 |
| `x` / `y` | 是 | 地图坐标，浮点数。不是屏幕像素，是游戏世界坐标 |
| `type` | 是 | `path` / `target` / `teleport` / `orientation`，见下 |
| `move_mode` | 否 | 到下一个点之前的移动方式，默认 `walk` |
| `action` | 否 | 到达这个点时执行的动作，见第 7 节 |
| `action_params` | 否 | 动作参数，格式随 `action` 变 |
| `items` | 否 | `[{ "name": "...", "count": 1 }]`，该点采集/击杀目标 |
| `point_ext_params` | 否 | 高级参数，见第 8 节 |

### `type` 的四种取值

| 值 | 含义 | 适用 |
| --- | --- | --- |
| `teleport` | 传送点。任务开头必须有一个，否则角色不知道自己在哪 | 每组路线的第一个点 |
| `path` | 途径点。走上去即可，不做额外判定 | 大部分点 |
| `target` | 目标点。到位后允许执行 `action`，且放宽到点判定 | 采集、战斗、交互 |
| `orientation` | 方位点。只用来校准朝向，不要求走到 | 进洞、转向、进门之前 |

一段路线里，`teleport` 之后要跟 `path`/`target` 才走得动。`orientation` 点附近不要放 `action`。

### `move_mode` 的七种

`walk` 步行 · `run` 奔跑 · `dash` 持续冲刺 · `climb` 攀爬 · `fly` 飞行 · `jump` 跳跃 · `swim` 游泳

`move_mode` 描述的是**从当前点到下一个点**的过程，写在当前点上。常见的坑是能跳过去的地方写了 `walk`，角色会卡在崖边来回蹭。

---

## 6. 坐标从哪来

三种方式，从省事到精确：

1. **录制**（推荐）：地图追踪界面点「录制」，手动走一遍，自动生成 `positions`。
2. **大地图取点**：录制时在地图上点目标位置，直接取到世界坐标。
3. **校准工具**：仓库里的校准页面可以加载任务、在地图上标注和传送，改完直接写回文件。适合批量修老路线。

坐标是**世界坐标**，换分辨率、换窗口大小都不受影响 —— 这一点跟屏幕像素不同，别拿截图量。

---

## 7. 动作 `action`

| 值 | 作用 | `action_params` |
| --- | --- | --- |
| `pick_up_collect` | 聚集材料（接近后集中拾取） | 角色名或指令，逗号分隔；留空则自动选 |
| `pick_around` | 在周围转圈拾取 | 转几圈，整数，默认 1 |
| `fight` | 战斗 | 空。队伍配置里选战斗策略 |
| `combat_script` | 用指定战斗策略脚本打一场 | 策略脚本路径，**必填** |
| `mining` | 挖矿 | `disablePickupAround` 可关掉挖完的周围拾取 |
| `linnea_mining` | 莉奈娅挖矿 | `矿点数,扫描轮数` |
| `hydro_collect` / `electro_collect` / `anemo_collect` / `pyro_collect` | 对应元素力采集 | 空 |
| `nahida_collect` | 纳西妲长按 E 收集 | 空 |
| `up_down_grab_leaf` | 四叶印 | `up` 或 `down` |
| `stop_flying` | 下落攻击落地 | 等待秒数，可选 |
| `force_tp` | 在当前点强制传送 | 空 |
| `use_gadget` | 使用小道具 | `not_wait` 不等待，或最大等待秒数 |
| `fishing` | 钓鱼 | 空 |
| `set_time` | 设置游戏时间 | `HH:mm`，如 `18:00` |
| `exit_and_relogin` | 退出重新登录（刷新世界） | 空 |
| `enter_and_exit_wonderland` | 进出尘歌壶 | 空 |
| `log_output` | 往日志里写一行 | 要输出的文本 |

`action` 只对 `type: "target"` 和部分 `Custom` 类动作生效；写在 `path` 点上会被忽略。

---

## 8. `point_ext_params`：识别异常怎么兜底

小地图识别偶尔会抽风（被 UI 遮挡、传送到新区域、地形太像）。这个对象控制单个点出错时的处理：

```json
"point_ext_params": {
  "description": "洞口，容易被树挡",
  "misidentification": {
    "type": ["unrecognized", "path_too_far"],
    "handling_mode": "previous_detected_point",
    "arrival_time": 0
  },
  "monster_tag": "elite",
  "enable_monster_loot_split": false
}
```

| 字段 | 说明 |
| --- | --- |
| `misidentification.type` | 触发条件：`unrecognized` 没认出来 / `path_too_far` 偏差过大 |
| `misidentification.handling_mode` | `previous_detected_point` 取上一个识别到的位置 / `map_recognition` 用大地图重新定位 / `scheduled_arrival` 按预估时间直接算到达 |
| `misidentification.arrival_time` | `scheduled_arrival` 用，秒 |
| `monster_tag` | `normal` / `elite` / `legendary`，影响战斗策略选择 |
| `description` | 备注，只给自己看 |

不确定就别写，默认值已经够用。`map_recognition` 会打断当前移动去开大地图，只在真的走丢时才值得开。

---

## 9. 队伍与战斗

路线本身**不带队伍信息**，队伍在「地图追踪」的任务配置里选：

| 项 | 说明 |
| --- | --- |
| 队伍名 | 进本前切换到的队伍，留空则不切 |
| 行走位 / 生存位 | 队伍里第几个角色负责跑图和扛伤（从 1 开始） |
| 战斗策略 | `fight` / `combat_script` 动作使用的策略脚本，不填用默认策略 |
| 自动拾取 | 是否开自动拾取，以及黑白名单 |
| 回复时机 | `AnyWaypoint` 任意点 / `OnlyTeleport` 只在传送点 / `Never` 不回复 |

想给不同秘境配不同队伍，可以拆成多份路线文件，或者用调度器按任务分别指定配置。

---

## 10. 调试

| 现象 | 先看哪 |
| --- | --- |
| 一启动就提示版本不符 | `info.bgi_version` 写高了 |
| 角色原地不动 | 第一个点不是 `teleport`，或 `path_name` 与 `map_name` 不匹配 |
| 反复卡在同一个点 | 这个点前面缺 `orientation`，或者 `move_mode` 不适合地形 |
| 一直提示「未识别」 | 该点被 UI 遮挡；加 `misidentification.map_recognition` |
| 采集完不走 | `action` 挂了但 `type` 是 `path`，改成 `target` |

界面上有「显示路径点」和运行日志，调坐标时都打开。日志里每个点会打印识别到的实际坐标，跟文件里的对比就知道偏了多少。

---

## 11. 分发

路线任务就是普通 JSON 文件，压缩成 zip 发给别人，对方解压到 `User/AutoPathing/` 即可。也能通过「脚本仓库」分发（同一套仓库机制），但路线不参与脚本的 `manifest.json` 校验。

> 想让自己写的路线出现在别人的软件里，最省事的方式是放进自己的脚本仓库，而不是塞进主仓库。
