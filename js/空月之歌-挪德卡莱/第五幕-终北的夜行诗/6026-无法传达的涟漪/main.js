// ============================================================
// 6026 无法传达的涟漪 — BetterGI 自动跑图脚本
// 幕次：空月之歌 第五幕·终北的夜行诗
//
// 安装：把本文件夹整个复制到 <BetterGI>\User\JsScript\ 下
//       或在仓库根目录执行  install.ps1 -BgiRoot "D:\BetterGI"
// 路线：pathing\*.json，可用「地图追踪」单独打开某一段调试
//
// 做什么：逐段传送到首点附近 → 沿路线跑图 → 自动跳过剧情对话与选项
// 不做什么：不解谜、不战斗、不判断任务是否已激活
// ============================================================

const CONFIG = {
    startSeg: 0,               // 从第几段开始（0 = 从头），断点续跑改这里
    skipSegs: [],              // 要跳过的段号，空数组表示全跑
    teleportBeforeSeg: true,   // 每段开始前先传送到首点附近
    autoSkip: true,            // 自动跳对话 + 自动选项
    chooseOption: '优先选择第一个选项',
    delayAfterTeleport: 2500,  // 传送后等待（毫秒）
    delayAfterSeg: 2000,       // 每段结束后等待，让剧情先推进
    stopOnFail: false,         // 某段失败时是否中止整个脚本
};

// 路线清单，file 相对本脚本目录
const SEGMENTS = [
    {
            "seq": 1,
        "map": "Teyvat",
        "file": "pathing/01.json",
        "label": "去见哥伦比娅 → 去见哥伦比娅",
        "points": 1
    },
    {
            "seq": 2,
        "map": "Teyvat",
        "file": "pathing/02.json",
        "label": "去那夏镇收集情报 → 去那夏镇收集情报",
        "points": 1
    },
    {
            "seq": 3,
        "map": "Teyvat",
        "file": "pathing/03.json",
        "label": "前往「银月之庭」 → 思考接下来的行动",
        "points": 114
    },
    {
            "seq": 4,
        "map": "Teyvat",
        "file": "pathing/04.json",
        "label": "继续和艾莉丝交谈 → 在那夏镇漫步",
        "points": 10
    },
    {
            "seq": 5,
        "map": "Teyvat",
        "file": "pathing/05.json",
        "label": "前去寻找魔女尼可 → 在「霜月之坊」寻找资料",
        "points": 24
    },
    {
            "seq": 6,
        "map": "Teyvat",
        "file": "pathing/06.json",
        "label": "前往会见愚人众执行官 → 前往会见愚人众执行官",
        "points": 1
    },
    {
            "seq": 7,
        "map": "Teyvat",
        "file": "pathing/07.json",
        "label": "调查虚影 → 保护拉撒抹培",
        "points": 68
    },
    {
            "seq": 8,
        "map": "Teyvat",
        "file": "pathing/08.json",
        "label": "抢在拉撒抹培前封锁大门 → 与拉撒抹培交谈",
        "points": 18
    }
];

function pickSegments() {
    return SEGMENTS.filter(function (s) {
        return s.seq >= CONFIG.startSeg && CONFIG.skipSegs.indexOf(s.seq) < 0;
    });
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
        log.error('没有可执行的路线段，请检查 CONFIG.skipSegs');
        return;
    }

    log.info('6026 无法传达的涟漪：共 ' + list.length + ' 段');
    installAutoSkip();

    let ok = 0, bad = 0;
    for (let i = 0; i < list.length; i++) {
        const seg = list[i];
        log.info('[' + (i + 1) + '/' + list.length + '] 第 ' + seg.seq + ' 段（' + seg.map + '）');
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

        if (!data.positions || data.positions.length < 2) {
            // 只有 1 个点的是任务提示坐标，不是能走的路线：硬跑会直冲目标，常停在副本门口或墙边
            bad++;
            log.warn('  段 ' + seg.seq + ' 只有 ' + ((data.positions || []).length) + ' 个点，是任务提示点而非路线，已跳过');
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
        notification.send('6026 无法传达的涟漪：' + ok + ' 段完成，' + bad + ' 段需手动');
    } catch (e) { }
}

// 脚本模式不允许顶层 await，用 IIFE 返回 Promise 交给宿主等待
(async () => {
    try {
        await main();
    } finally {
        try { dispatcher.clearAllTriggers(); } catch (e) { }
    }
})();
