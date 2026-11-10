// ============================================================
// 风和自由（7029）—— BetterGI 自动跑图
// 幕次：第六幕 ｜ 共 4 段 / 10 个目标点
// 数据源：AnimeGameData CNRELWin 7.2.0 ｜ 生成于 2026-11-10
//
// 做什么：按段传送到起点附近，沿任务目标点自动跑图，途中自动跳过剧情对话。
// 不做什么：不解谜、不战斗、不判断任务是否已激活。详见仓库根目录 README.md。
//
// 用法：BetterGI → 脚本 → 新建 → 粘贴本文件 → 保存 → 游戏内运行
// ============================================================

const CONFIG = {
    quests: ['7029'],
    skipSegs: [],          // 跳过某些段（空数组表示全跑）
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
  {qid:"7029",name:"风和自由",act:"第六幕",map:"Teyvat",seq:1,total:4,from:"前往蒙德城",to:"前往骑士团",points:[[2250.6, -894.0], [2243.4, -778.5]]},
  {qid:"7029",name:"风和自由",act:"第六幕",map:"Teyvat",seq:2,total:4,from:"赶赴龙脊雪山",to:"观察丘丘人的行为，收集情报",points:[[1597.6, -1082.9], [1328.0, -1013.9], [1175.1, -1022.4], [1171.2, -1018.5]]},
  {qid:"7029",name:"风和自由",act:"第六幕",map:"Teyvat",seq:3,total:4,from:"返回骑士团分享侦察结果",to:"返回骑士团分享侦察结果",points:[[2243.4, -778.5]]},
  {qid:"7029",name:"风和自由",act:"第六幕",map:"Teyvat",seq:4,total:4,from:"前往龙脊雪山",to:"完成「僭续试炼」",points:[[1534.2, -1086.2], [1537.6, -1090.7], [1534.2, -1086.2]]},
];

// ------------------------------------------------------------
// 工具
// ------------------------------------------------------------
function segments() {
    return ROUTES.filter(r => r.qid === '7029' && CONFIG.skipSegs.indexOf(r.seq) < 0)
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
    log.info(`风和自由：共 ${list.length} 段 / ${totalPts} 个目标点`);
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
        notification.send(`风和自由：${ok} 段完成，${bad} 段需手动`);
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
