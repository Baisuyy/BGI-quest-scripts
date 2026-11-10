// ============================================================
// 迈向未来的一步（7031）—— BetterGI 自动跑图
// 幕次：第五幕·前情 ｜ 共 4 段 / 15 个目标点
// 数据源：AnimeGameData CNRELWin 7.2.0 ｜ 生成于 2026-11-10
//
// 做什么：按段传送到起点附近，沿任务目标点自动跑图，途中自动跳过剧情对话。
// 不做什么：不解谜、不战斗、不判断任务是否已激活。详见仓库根目录 README.md。
//
// 用法：BetterGI → 脚本 → 新建 → 粘贴本文件 → 保存 → 游戏内运行
// ============================================================

const CONFIG = {
    quests: ['7031'],
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
  {qid:"7031",name:"迈向未来的一步",act:"第五幕·前情",map:"Teyvat",seq:1,total:4,from:"前往空之神殿",to:"前往空之神殿",points:[[9661.0, 6579.0]]},
  {qid:"7031",name:"迈向未来的一步",act:"第五幕·前情",map:"TempleOfSpace",seq:2,total:4,from:"与尼可、艾莉丝对话",to:"寻找并收集符文",points:[[261.6, 332.3], [454.1, 251.8], [501.7, 235.1], [327.0, 531.0], [353.0, 499.0], [415.0, 495.0], [362.9, 503.8], [478.3, 552.8]]},
  {qid:"7031",name:"迈向未来的一步",act:"第五幕·前情",map:"TempleOfSpace",seq:3,total:4,from:"寻找并收集符文",to:"与光之镜对话",points:[[307.4, 550.2], [414.3, 631.0], [213.5, 464.9], [454.1, 251.8], [501.7, 235.1]]},
  {qid:"7031",name:"迈向未来的一步",act:"第五幕·前情",map:"Teyvat",seq:4,total:4,from:"等待到几天后",to:"等待到几天后",points:[[9644.9, 6615.6]]},
];

// ------------------------------------------------------------
// 工具
// ------------------------------------------------------------
function segments() {
    return ROUTES.filter(r => r.qid === '7031' && CONFIG.skipSegs.indexOf(r.seq) < 0)
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
    log.info(`迈向未来的一步：共 ${list.length} 段 / ${totalPts} 个目标点`);
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
        notification.send(`迈向未来的一步：${ok} 段完成，${bad} 段需手动`);
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
