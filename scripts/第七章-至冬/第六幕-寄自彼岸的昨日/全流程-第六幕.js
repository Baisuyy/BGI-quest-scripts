// ============================================================
// 寄自彼岸的昨日（第七章 第六幕）—— BetterGI 自动跑图脚本
// 数据源：AnimeGameData CNRELWin 7.2.0 ｜ 生成于 2026-11-10
//
// 它做什么：
//   按任务顺序，传送到每段起点，再沿任务目标点自动跑图；
//   途中开启「自动剧情」，自动跳过对话、自动选选项。
// 它不做什么：
//   不解谜、不打试炼、不判断任务是否已激活。详见同目录 README.md。
//
// 用法：BetterGI → 脚本 → 新建 → 粘贴本文件 → 保存 → 游戏内运行
// ============================================================

const CONFIG = {
    // 要执行的任务，按数组顺序跑。'7030','7031' 是第五幕收尾（前情），需要时再加
    quests: ['7023', '7025', '7026', '7027', '7029', '7028'],

    teleportBeforeSeg: true,   // 每段开始前先传送到首点附近
    autoSkip: true,            // 开启自动剧情（跳过对话 + 自动选项）
    chooseOption: '优先选择第一个选项',
    moveMode: 'run',           // walk / run / dash
    delayAfterTeleport: 2500,  // 传送后等待（毫秒）
    delayAfterSeg: 2500,       // 每段结束后等待，让剧情先推进
    stopOnFail: false,         // 某段失败时是否中止整个脚本
    rangeFrom: 0,              // 从第几段开始（1 起，0 = 第一段）
    rangeTo: 0,                // 到第几段结束（含，0 = 最后一段）
};

// 由任务数据生成的路线：points = [[地图X, 地图Y], ...]（游戏坐标的 X 与 Z）
const ROUTES = [
  {qid:"7030",name:"如同消融的积雪",act:"第五幕·前情",map:"Teyvat",seq:1,total:3,from:"前往至冬堡",to:"前往至冬堡",points:[[9654.5, 6606.9]]},
  {qid:"7030",name:"如同消融的积雪",act:"第五幕·前情",map:"Teyvat",seq:2,total:3,from:"前往雪桦车站",to:"乘坐火车前往贝洛沃迪",points:[[9329.3, 4821.2], [9322.5, 4797.7], [9592.7, 4322.1], [9667.4, 4286.5], [9622.0, 4336.8], [9647.1, 4273.7]]},
  {qid:"7030",name:"如同消融的积雪",act:"第五幕·前情",map:"Teyvat",seq:3,total:3,from:"击败涌出的魔物",to:"与此同时，贝洛沃迪车站…",points:[[8789.2, 2918.8], [8789.6, 2934.7], [9061.4, 3454.8]]},
  {qid:"7031",name:"迈向未来的一步",act:"第五幕·前情",map:"Teyvat",seq:1,total:4,from:"前往空之神殿",to:"前往空之神殿",points:[[9661.0, 6579.0]]},
  {qid:"7031",name:"迈向未来的一步",act:"第五幕·前情",map:"TempleOfSpace",seq:2,total:4,from:"与尼可、艾莉丝对话",to:"寻找并收集符文",points:[[261.6, 332.3], [454.1, 251.8], [501.7, 235.1], [327.0, 531.0], [353.0, 499.0], [415.0, 495.0], [362.9, 503.8], [478.3, 552.8]]},
  {qid:"7031",name:"迈向未来的一步",act:"第五幕·前情",map:"TempleOfSpace",seq:3,total:4,from:"寻找并收集符文",to:"与光之镜对话",points:[[307.4, 550.2], [414.3, 631.0], [213.5, 464.9], [454.1, 251.8], [501.7, 235.1]]},
  {qid:"7031",name:"迈向未来的一步",act:"第五幕·前情",map:"Teyvat",seq:4,total:4,from:"等待到几天后",to:"等待到几天后",points:[[9644.9, 6615.6]]},
  {qid:"7023",name:"僭越者的试炼",act:"第六幕",map:"Teyvat",seq:1,total:1,from:"回到至冬宫分享与更新情报",to:"知晓女皇的权能",points:[[9654.2, 6607.5], [9789.6, 6492.6], [9744.3, 6638.3], [9631.5, 6603.7]]},
  {qid:"7025",name:"雷和永恒",act:"第六幕",map:"Teyvat",seq:1,total:3,from:"前往稻妻城",to:"前往天守阁了解研究进展",points:[[-3063.0, -4409.1], [-3344.0, -4553.8], [-3378.9, -4581.5], [-3113.1, -4384.6], [-3379.1, -4583.0]]},
  {qid:"7025",name:"雷和永恒",act:"第六幕",map:"Teyvat",seq:2,total:3,from:"出发前往清籁岛",to:"暂时令原始雷元素力平静下来",points:[[-4241.9, -4087.8], [-4313.9, -4067.0], [-4316.2, -4077.2], [-4331.4, -4107.1], [-4407.7, -4205.8], [-4708.4, -4259.7], [-4720.1, -4263.1]]},
  {qid:"7025",name:"雷和永恒",act:"第六幕",map:"Teyvat",seq:3,total:3,from:"返回天守阁",to:"返回天守阁",points:[[-3422.5, -4614.8]]},
  {qid:"7026",name:"草和智慧",act:"第六幕",map:"Teyvat",seq:1,total:4,from:"前往须弥城",to:"去净善宫见纳西妲",points:[[-454.5, 2765.0], [-368.0, 2896.8], [-369.4, 2902.2]]},
  {qid:"7026",name:"草和智慧",act:"第六幕",map:"Teyvat",seq:2,total:4,from:"与纳西妲同去判罚之钉的附近",to:"与纳西妲同去判罚之钉的附近",points:[[-1160.3, 5444.0]]},
  {qid:"7026",name:"草和智慧",act:"第六幕",map:"Teyvat",seq:3,total:4,from:"将声音的线索带回须弥城",to:"将声音的线索带回须弥城",points:[[-380.2, 2934.6]]},
  {qid:"7026",name:"草和智慧",act:"第六幕",map:"Teyvat",seq:4,total:4,from:"前去使用「阿如」的力量",to:"与纳西妲交谈",points:[[-2345.5, 5560.4], [-2345.5, 5554.9], [-2259.6, 5544.1]]},
  {qid:"7027",name:"火和战争",act:"第六幕",map:"Teyvat",seq:1,total:8,from:"前往圣火竞技场",to:"去拜访茜特菈莉",points:[[-1872.5, 8994.0], [-1957.5, 9091.6], [-2006.3, 9642.8]]},
  {qid:"7027",name:"火和战争",act:"第六幕",map:"Teyvat",seq:2,total:8,from:"前往任意部族的曜石图腾柱",to:"前往任意部族的曜石图腾柱",points:[[-1342.2, 7652.2]]},
  {qid:"7027",name:"火和战争",act:"第六幕",map:"Teyvat",seq:3,total:8,from:"前往任意部族的曜石图腾柱",to:"前往任意部族的曜石图腾柱",points:[[-2107.9, 8430.7]]},
  {qid:"7027",name:"火和战争",act:"第六幕",map:"Teyvat",seq:4,total:8,from:"前往任意部族的曜石图腾柱",to:"前往任意部族的曜石图腾柱",points:[[-2918.9, 9033.3]]},
  {qid:"7027",name:"火和战争",act:"第六幕",map:"Teyvat",seq:5,total:8,from:"前往任意部族的曜石图腾柱",to:"前往任意部族的曜石图腾柱",points:[[-1205.5, 9485.8], [-1649.0, 9709.8]]},
  {qid:"7027",name:"火和战争",act:"第六幕",map:"Teyvat",seq:6,total:8,from:"前往任意部族的曜石图腾柱",to:"前往任意部族的曜石图腾柱",points:[[-2718.5, 10264.6]]},
  {qid:"7027",name:"火和战争",act:"第六幕",map:"Teyvat",seq:7,total:8,from:"准备启动驰轮车",to:"准备启动驰轮车",points:[[-1657.4, 10015.3]]},
  {qid:"7027",name:"火和战争",act:"第六幕",map:"Teyvat",seq:8,total:8,from:"返回圣火竞技场",to:"返回圣火竞技场",points:[[-1906.2, 9066.2]]},
  {qid:"7029",name:"风和自由",act:"第六幕",map:"Teyvat",seq:1,total:4,from:"前往蒙德城",to:"前往骑士团",points:[[2250.6, -894.0], [2243.4, -778.5]]},
  {qid:"7029",name:"风和自由",act:"第六幕",map:"Teyvat",seq:2,total:4,from:"赶赴龙脊雪山",to:"观察丘丘人的行为，收集情报",points:[[1597.6, -1082.9], [1328.0, -1013.9], [1175.1, -1022.4], [1171.2, -1018.5]]},
  {qid:"7029",name:"风和自由",act:"第六幕",map:"Teyvat",seq:3,total:4,from:"返回骑士团分享侦察结果",to:"返回骑士团分享侦察结果",points:[[2243.4, -778.5]]},
  {qid:"7029",name:"风和自由",act:"第六幕",map:"Teyvat",seq:4,total:4,from:"前往龙脊雪山",to:"完成「僭续试炼」",points:[[1534.2, -1086.2], [1537.6, -1090.7], [1534.2, -1086.2]]},
  {qid:"7028",name:"叩响终幕之门",act:"第六幕",map:"Teyvat",seq:1,total:1,from:"前往光芒汇聚之地",to:"离开神之座，讨论现状",points:[[1602.4, 2876.6], [1622.2, 2915.9], [1602.4, 2876.6], [1599.5, 2878.6]]}
];

// ------------------------------------------------------------
// 工具
// ------------------------------------------------------------
const questOrder = (qid) => CONFIG.quests.indexOf(qid);

function segments() {
    return ROUTES
        .filter(r => questOrder(r.qid) >= 0)
        .sort((a, b) => questOrder(a.qid) - questOrder(b.qid) || a.seq - b.seq);
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
        // 传送失败不致命，交给寻路自己走过去
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
    cfg.AutoGetDailyRewardsEnabled = false;   // 跑图时不要顺手领奖励
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
    let list = segments();
    if (CONFIG.rangeFrom > 0 || CONFIG.rangeTo > 0) {
        const a = CONFIG.rangeFrom > 0 ? CONFIG.rangeFrom - 1 : 0;
        const b = CONFIG.rangeTo > 0 ? CONFIG.rangeTo : list.length;
        list = list.slice(a, b);
    }
    if (list.length === 0) {
        log.error('没有匹配的路线，请检查 CONFIG.quests 中的任务号');
        return;
    }

    const totalPts = list.reduce((s, r) => s + r.points.length, 0);
    log.info(`路线共 ${list.length} 段 / ${totalPts} 个目标点`);
    installAutoSkip();

    let ok = 0;
    let bad = 0;

    for (let i = 0; i < list.length; i++) {
        const seg = list[i];
        log.info(`[${i + 1}/${list.length}] ${seg.qid} ${seg.name} ${seg.seq}/${seg.total}（${seg.map}）`);
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
        notification.send(`寄自彼岸的昨日：${ok} 段完成，${bad} 段需手动`);
    } catch (e) { }
}

// 立即执行函数：脚本模式下不允许顶层 await，返回的 Promise 会被宿主等待
(async () => {
    try {
        await main();
    } finally {
        // 无论正常结束还是被停止，都清掉本次加的实时任务
        try { dispatcher.clearAllTriggers(); } catch (e) { }
    }
})();
