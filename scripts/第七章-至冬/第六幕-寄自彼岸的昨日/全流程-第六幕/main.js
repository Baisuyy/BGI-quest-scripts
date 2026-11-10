// ============================================================
// 第六幕 全流程（7023–7029）— BetterGI 自动跑图脚本
// 幕次：第七章 第六幕·寄自彼岸的昨日
//
// 安装：把本文件夹整个复制到 <BetterGI>\User\JsScript\ 下
//       或在仓库根目录执行  install.ps1 -BgiRoot "D:\BetterGI"
// 路线：pathing\<任务ID>\*.json，可用「地图追踪」单独打开某一段调试
//
// 按 CONFIG.quests 的顺序连跑；想只跑其中几个任务，改这个数组即可
// ============================================================

const CONFIG = {
    quests: ['7023', '7025', '7026', '7027', '7029', '7028'],   // 空数组 = 全部按顺序
    startFrom: '',             // 断点续跑：形如 '7027/3'，从该段开始；空字符串表示从头
    skipSegs: [],            // 要跳过的段，写 '7027/5' 这种形式
    teleportBeforeSeg: true,
    autoSkip: true,
    chooseOption: '优先选择第一个选项',
    delayAfterTeleport: 2500,
    delayAfterSeg: 2000,
    stopOnFail: false,
};

const SEGMENTS = [
    {
        "qid": "7023",
        "task": "僭越者的试炼",
        "seq": 1,
        "map": "Teyvat",
        "file": "pathing/7023/01.json",
        "label": "回到至冬宫分享与更新情报 → 知晓女皇的权能"
    },
    {
        "qid": "7025",
        "task": "雷和永恒",
        "seq": 1,
        "map": "Teyvat",
        "file": "pathing/7025/01.json",
        "label": "前往稻妻城 → 前往天守阁了解研究进展"
    },
    {
        "qid": "7025",
        "task": "雷和永恒",
        "seq": 2,
        "map": "Teyvat",
        "file": "pathing/7025/02.json",
        "label": "出发前往清籁岛 → 暂时令原始雷元素力平静下来"
    },
    {
        "qid": "7025",
        "task": "雷和永恒",
        "seq": 3,
        "map": "Teyvat",
        "file": "pathing/7025/03.json",
        "label": "返回天守阁 → 返回天守阁"
    },
    {
        "qid": "7026",
        "task": "草和智慧",
        "seq": 1,
        "map": "Teyvat",
        "file": "pathing/7026/01.json",
        "label": "前往须弥城 → 去净善宫见纳西妲"
    },
    {
        "qid": "7026",
        "task": "草和智慧",
        "seq": 2,
        "map": "Teyvat",
        "file": "pathing/7026/02.json",
        "label": "与纳西妲同去判罚之钉的附近 → 与纳西妲同去判罚之钉的附近"
    },
    {
        "qid": "7026",
        "task": "草和智慧",
        "seq": 3,
        "map": "Teyvat",
        "file": "pathing/7026/03.json",
        "label": "将声音的线索带回须弥城 → 将声音的线索带回须弥城"
    },
    {
        "qid": "7026",
        "task": "草和智慧",
        "seq": 4,
        "map": "Teyvat",
        "file": "pathing/7026/04.json",
        "label": "前去使用「阿如」的力量 → 与纳西妲交谈"
    },
    {
        "qid": "7027",
        "task": "火和战争",
        "seq": 1,
        "map": "Teyvat",
        "file": "pathing/7027/01.json",
        "label": "前往圣火竞技场 → 去拜访茜特菈莉"
    },
    {
        "qid": "7027",
        "task": "火和战争",
        "seq": 2,
        "map": "Teyvat",
        "file": "pathing/7027/02.json",
        "label": "前往任意部族的曜石图腾柱 → 前往任意部族的曜石图腾柱"
    },
    {
        "qid": "7027",
        "task": "火和战争",
        "seq": 3,
        "map": "Teyvat",
        "file": "pathing/7027/03.json",
        "label": "前往任意部族的曜石图腾柱 → 前往任意部族的曜石图腾柱"
    },
    {
        "qid": "7027",
        "task": "火和战争",
        "seq": 4,
        "map": "Teyvat",
        "file": "pathing/7027/04.json",
        "label": "前往任意部族的曜石图腾柱 → 前往任意部族的曜石图腾柱"
    },
    {
        "qid": "7027",
        "task": "火和战争",
        "seq": 5,
        "map": "Teyvat",
        "file": "pathing/7027/05.json",
        "label": "前往任意部族的曜石图腾柱 → 前往任意部族的曜石图腾柱"
    },
    {
        "qid": "7027",
        "task": "火和战争",
        "seq": 6,
        "map": "Teyvat",
        "file": "pathing/7027/06.json",
        "label": "前往任意部族的曜石图腾柱 → 前往任意部族的曜石图腾柱"
    },
    {
        "qid": "7027",
        "task": "火和战争",
        "seq": 7,
        "map": "Teyvat",
        "file": "pathing/7027/07.json",
        "label": "准备启动驰轮车 → 准备启动驰轮车"
    },
    {
        "qid": "7027",
        "task": "火和战争",
        "seq": 8,
        "map": "Teyvat",
        "file": "pathing/7027/08.json",
        "label": "返回圣火竞技场 → 返回圣火竞技场"
    },
    {
        "qid": "7028",
        "task": "叩响终幕之门",
        "seq": 1,
        "map": "Teyvat",
        "file": "pathing/7028/01.json",
        "label": "前往光芒汇聚之地 → 离开神之座，讨论现状"
    },
    {
        "qid": "7029",
        "task": "风和自由",
        "seq": 1,
        "map": "Teyvat",
        "file": "pathing/7029/01.json",
        "label": "前往蒙德城 → 前往骑士团"
    },
    {
        "qid": "7029",
        "task": "风和自由",
        "seq": 2,
        "map": "Teyvat",
        "file": "pathing/7029/02.json",
        "label": "赶赴龙脊雪山 → 观察丘丘人的行为，收集情报"
    },
    {
        "qid": "7029",
        "task": "风和自由",
        "seq": 3,
        "map": "Teyvat",
        "file": "pathing/7029/03.json",
        "label": "返回骑士团分享侦察结果 → 返回骑士团分享侦察结果"
    },
    {
        "qid": "7029",
        "task": "风和自由",
        "seq": 4,
        "map": "Teyvat",
        "file": "pathing/7029/04.json",
        "label": "前往龙脊雪山 → 完成「僭续试炼」"
    }
];

function pickSegments() {
    let list = SEGMENTS.slice();
    if (CONFIG.quests.length > 0) {
        list = list.filter(function (s) { return CONFIG.quests.indexOf(s.qid) >= 0; });
        list.sort(function (a, b) {
            const qa = CONFIG.quests.indexOf(a.qid), qb = CONFIG.quests.indexOf(b.qid);
            return qa !== qb ? qa - qb : a.seq - b.seq;
        });
    }
    list = list.filter(function (s) { return CONFIG.skipSegs.indexOf(s.qid + '/' + s.seq) < 0; });
    if (CONFIG.startFrom) {
        const from = list.findIndex(function (s) { return s.qid + '/' + s.seq === CONFIG.startFrom; });
        if (from < 0) log.warn('startFrom 指向的段不存在：' + CONFIG.startFrom + '，将从头执行');
        else list = list.slice(from);
    }
    return list;
}

function loadPathing(seg) {
    const text = file.readTextSync(seg.file);
    if (!text) throw new Error('读不到路线文件：' + seg.file);
    const data = JSON.parse(text);
    if (!data.positions || data.positions.length === 0) throw new Error('路线没有点位：' + seg.file);
    return data;
}

async function teleportTo(seg, data) {
    const p = data.positions[0];
    try {
        if (seg.map === 'Teyvat') {
            await genshin.tp(p.x, p.y);
        } else {
            await genshin.tp(p.x, p.y, seg.map, false);
        }
        log.info('  已传送至 ' + seg.map + '（' + Math.round(p.x) + ', ' + Math.round(p.y) + '）');
        return true;
    } catch (e) {
        log.warn('  传送失败（' + seg.map + ' ' + p.x + ',' + p.y + '）：' + e.message);
        return false;
    }
}

function installAutoSkip() {
    if (!CONFIG.autoSkip) return;
    const cfg = new AutoSkipConfig();
    cfg.Enabled = true;
    cfg.QuicklySkipConversationsEnabled = true;
    cfg.ClickChatOption = CONFIG.chooseOption;
    cfg.AutoGetDailyRewardsEnabled = false;
    cfg.AutoReExploreEnabled = false;

    const timer = new RealtimeTimer('AutoSkip');
    timer.Config = cfg;
    dispatcher.addTrigger(timer);
    log.info('自动剧情已开启（跳对话 + 自动选项）');
}

async function main() {
    const list = pickSegments();
    if (list.length === 0) {
        log.error('没有可执行的路线段，请检查 CONFIG.quests / skipSegs');
        return;
    }

    log.info('第六幕全流程：' + CONFIG.quests.join(' → ') + '，共 ' + list.length + ' 段');
    installAutoSkip();

    let ok = 0, bad = 0, lastTask = '';
    for (let i = 0; i < list.length; i++) {
        const seg = list[i];
        if (seg.qid !== lastTask) {
            lastTask = seg.qid;
            log.info('=== ' + seg.qid + ' ' + seg.task + ' ===');
        }
        log.info('[' + (i + 1) + '/' + list.length + '] ' + seg.qid + ' 第 ' + seg.seq + ' 段（' + seg.map + '）');
        log.info('  ' + seg.label);

        let data;
        try {
            data = loadPathing(seg);
        } catch (e) {
            bad++;
            log.error('  路线加载失败：' + e.message);
            if (CONFIG.stopOnFail) break;
            continue;
        }

        if (CONFIG.teleportBeforeSeg) {
            await teleportTo(seg, data);
            await sleep(CONFIG.delayAfterTeleport);
        }

        try {
            const success = await pathingScript.runFile(seg.file);
            if (success) {
                ok++;
                log.info('  本段完成');
            } else {
                bad++;
                log.warn('  本段未走完（可能卡住或超时），可手动走到目标点后继续');
                if (CONFIG.stopOnFail) break;
            }
        } catch (e) {
            bad++;
            log.error('  本段异常：' + e.message);
            if (CONFIG.stopOnFail) break;
        }

        await sleep(CONFIG.delayAfterSeg);
    }

    log.info('结束：成功 ' + ok + ' 段，失败 ' + bad + ' 段');
    try {
        notification.send('第六幕全流程：' + ok + ' 段完成，' + bad + ' 段需手动');
    } catch (e) { }
}

(async () => {
    try {
        await main();
    } finally {
        try { dispatcher.clearAllTriggers(); } catch (e) { }
    }
})();
