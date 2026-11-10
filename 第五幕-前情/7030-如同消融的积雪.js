// ============================================================
// 如同消融的积雪（7030）—— BetterGI 自动跑图
// 幕次：第五幕·前情 ｜ 共 3 段 / 10 个目标点
// 数据源：AnimeGameData CNRELWin 7.2.0 ｜ 生成于 2026-11-10
//
// 做什么：按段传送到起点附近，沿任务目标点自动跑图，途中自动跳过剧情对话。
// 不做什么：不解谜、不战斗、不判断任务是否已激活。详见仓库根目录 README.md。
//
// 用法：BetterGI → 脚本 → 新建 → 粘贴本文件 → 保存 → 游戏内运行
// ============================================================

const CONFIG = {
    quests: ['7030'],
    skipSegs: [2, 3],          // 跳过某些段（空数组表示全跑）
    teleportBeforeSeg: true,   // 每段开始前先传送到首点附近
    autoSkip: true,            // 自动剧情（跳对话 + 自动选项）
    chooseOption: '优先选择第一个选项',
    moveMode: 'run',           // walk / run / dash
    delayAfterTeleport: 2500,
    delayAfterSeg: 2500,
    stopOnFail: false,
};

// 路线：points = [[地图X, 地图Y], ...]（游戏坐标的 X 与 Z）
const ROUTES = [
  {qid:"7030",name:"如同消融的积雪",act:"第五幕·前情",map:"Teyvat",seq:1,total:3,from:"前往至冬堡",to:"前往至冬堡",points:[[9654.5, 6606.9]]},
  {qid:"7030",name:"如同消融的积雪",act:"第五幕·前情",map:"Teyvat",seq:2,total:3,from:"前往雪桦车站",to:"乘坐火车前往贝洛沃迪",points:[[9329.3, 4821.2], [9322.5, 4797.7], [9592.7, 4322.1], [9667.4, 4286.5], [9622.0, 4336.8], [9647.1, 4273.7]]},
  {qid:"7030",name:"如同消融的积雪",act:"第五幕·前情",map:"Teyvat",seq:3,total:3,from:"击败涌出的魔物",to:"与此同时，贝洛沃迪车站…",points:[[8789.2, 2918.8], [8789.6, 2934.7], [9061.4, 3454.8]]},
];

// ------------------------------------------------------------
// 工具
// ------------------------------------------------------------
function segments() {
    return ROUTES.filter(r => r.qid === '7030' && CONFIG.skipSegs.indexOf(r.seq) < 0)
                 .sort((a, b) => a.seq - b.seq);
}

function buildJson(seg) {
    const positions = seg.points.map((p, i) => ({
        x: p[0],
        y: p[1],
        type: i === seg.points.length - 1 ? 'target' : 'path',
        move_mode: CONFIG.moveMode,
    }));
    return JSON.stringify({
        info: {
            name: `${seg.qid} ${seg.name} ${seg.seq}/${seg.total}`,
            description: `${seg.act}｜${seg.from} → ${seg.to}`,
            type: 'farming',
            order: seg.seq,
            map_name: seg.map,
            map_match_method: 'TemplateMatch',
        },
        // 剧情途中关掉自动拾取：拾取键和对话交互键都是 F，容易误触选项
        config: { realtime_triggers: { AutoPick: false } },
        positions: positions,
    });
}

async function teleportTo(seg) {
    const [x, y] = seg.points[0];
    try {
        if (seg.map === 'Teyvat') {
            await genshin.tp(x, y);
        } else {
            await genshin.tp(x, y, seg.map, false);
        }
        log.info(`  已传送至 ${seg.map} (${x}, ${y})`);
        return true;
    } catch (e) {
        log.warn(`  传送失败（${seg.map} ${x},${y}）：${e.message}`);
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

// ------------------------------------------------------------
// 主流程
// ------------------------------------------------------------
async function main() {
    const list = segments();
    if (list.length === 0) {
        log.error('没有可执行的路线段');
        return;
    }

    const totalPts = list.reduce((s, r) => s + r.points.length, 0);
    log.info(`如同消融的积雪：共 ${list.length} 段 / ${totalPts} 个目标点`);
    installAutoSkip();

    let ok = 0, bad = 0;
    for (let i = 0; i < list.length; i++) {
        const seg = list[i];
        log.info(`[${i + 1}/${list.length}] ${seg.seq}/${seg.total}（${seg.map}）`);
        log.info(`  ${seg.from} → ${seg.to}`);

        if (CONFIG.teleportBeforeSeg) {
            await teleportTo(seg);
            await sleep(CONFIG.delayAfterTeleport);
        }

        try {
            const success = await pathingScript.run(buildJson(seg));
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
            log.error(`  本段异常：${e.message}`);
            if (CONFIG.stopOnFail) break;
        }

        await sleep(CONFIG.delayAfterSeg);
    }

    log.info(`结束：成功 ${ok} 段，失败 ${bad} 段`);
    try {
        notification.send(`如同消融的积雪：${ok} 段完成，${bad} 段需手动`);
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
