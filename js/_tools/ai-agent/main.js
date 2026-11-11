// ============================================================
// AI 代理：BGI 当手脚，大模型当大脑
// ------------------------------------------------------------
//   感知  BGI 内置识别（小地图定位 / UI 状态 / F 键文字 / OCR）
//   决策  规则优先，规则拿不准时才问大模型
//   执行  pathingScript.Run / genshin.Tp / Bv.FindFAndPress
//
// 默认 ai.enabled = false，全程本地、不发任何网络请求。
// 普通版 BetterGI 能跑（感知弱一些）；装特别版后自动用上 ocr / quest。
// ============================================================

// ---------- 能力探测 ----------
const HAS = {
    bv: typeof Bv !== 'undefined',
    ocr: typeof ocr !== 'undefined',
    quest: typeof quest !== 'undefined',
};

// 特别版宿主：存在就用它，密钥、超时、次数都由客户端管
const HAS_AI = typeof ai !== 'undefined';

function call(obj, names) {
    const args = Array.prototype.slice.call(arguments, 2);
    for (let i = 0; i < names.length; i++) {
        const f = obj[names[i]];
        if (typeof f === 'function') return f.apply(obj, args);
    }
    throw new Error('找不到可调用方法：' + names.join(' / '));
}

function pick(obj, names) {
    for (let i = 0; i < names.length; i++) {
        if (obj && obj[names[i]] !== undefined) return obj[names[i]];
    }
    return undefined;
}

function val(v) {
    return (v === undefined || v === null) ? '' : String(v);
}

function release(r) {    if (!r) return;
    try {
        if (typeof r.Dispose === 'function') r.Dispose();
        else if (typeof r.dispose === 'function') r.dispose();
    } catch (e) { /* 忽略释放失败 */ }
}

// ---------- 配置 ----------
function loadConfig() {
    const def = {
        ai: { enabled: false, endpoint: '', model: '', apiKey: '', maxCalls: 30, temperature: 0.2 },
        vision: { mode: 'off' },   // off | multimodal：multimodal 时每步存一张整屏图，交给本地代理去编码
        goal: '',
        plan: [],
        maxSteps: 120,
        arriveRadius: 6,
        stuckSeconds: 5,
        maxGotoMeters: 150,
        saveProgress: true,
    };
    try {
        const raw = pick(file, ['ReadTextSync', 'readTextSync'])('config.json');
        const user = JSON.parse(raw);
        const ai = Object.assign({}, def.ai, user.ai || {});
        return Object.assign({}, def, user, { ai: ai });
    } catch (e) {
        log.warn('config.json 读取失败，使用默认值：' + e);
        return def;
    }
}

const CFG = loadConfig();

// ---------- 感知 ----------
const _trail = [];

function readPos() {
    try {
        const p = call(genshin, ['GetPositionFromMap', 'getPositionFromMap']);
        if (!p) return null;
        const x = pick(p, ['X', 'x']);
        const y = pick(p, ['Y', 'y']);
        if (x === undefined || y === undefined) return null;
        return { x: Math.round(x), y: Math.round(y) };
    } catch (e) {
        return null;   // 不在主界面、或小地图被遮挡
    }
}

function isStuck(pos) {
    if (!pos) return false;
    const now = Date.now();
    _trail.push({ t: now, x: pos.x, y: pos.y });
    while (_trail.length > 2 && now - _trail[0].t > CFG.stuckSeconds * 1000) _trail.shift();
    if (now - _trail[0].t < CFG.stuckSeconds * 900) return false;
    let spread = 0;
    for (let i = 0; i < _trail.length; i++) {
        spread = Math.max(spread, Math.hypot(_trail[i].x - pos.x, _trail[i].y - pos.y));
    }
    return spread < 2.5;
}

function snapshot() {
    const s = { pos: null, ui: '-', motion: '-', hint: '', quest: null, stuck: false };
    s.pos = readPos();

    let screen = null;
    try {
        screen = captureGameRegion();
        if (HAS.bv) {
            try { s.ui = String(call(Bv, ['WhichGameUi', 'whichGameUi'], screen)); } catch (e) { s.ui = '?'; }
            try { s.motion = String(call(Bv, ['GetMotionStatus', 'getMotionStatus'], screen)); } catch (e) { }
        }
        if (HAS.ocr) {
            try { s.hint = (call(ocr, ['ReadRegion', 'readRegion'], 760, 620, 400, 80) || '').trim(); } catch (e) { }
        } else if (HAS.bv) {
            try {
                const texts = ['调查', '拾取', '对话', '开启', '阅读', '进入', '乘坐'];
                const found = call(Bv, ['FindF', 'findF'], screen, texts);
                s.hint = found ? '有可交互提示' : '';
            } catch (e) { }
        }
    } finally {
        release(screen);
    }

    if (HAS.quest) {
        try {
            const t = call(quest, ['Tracker', 'tracker']);
            s.quest = { title: pick(t, ['Title', 'title']) || '', step: pick(t, ['Step', 'step']) || '' };
        } catch (e) { }
    }

    s.stuck = isStuck(s.pos);
    return s;
}

// ---------- 执行 ----------
function buildRoute(from, x, y, dist) {
    const n = Math.max(1, Math.ceil(dist / 8));
    const positions = [];
    for (let i = 1; i <= n; i++) {
        const t = i / n;
        positions.push({
            x: Math.round((from.x + (x - from.x) * t) * 10) / 10,
            y: Math.round((from.y + (y - from.y) * t) * 10) / 10,
            type: i === n ? 'target' : 'path',
            move_mode: 'run',
        });
    }
    return {
        info: {
            name: 'ai-goto',
            description: 'AI 直线纠偏 ' + Math.round(dist) + 'm',
            author: 'ai-agent',
            version: '1.0.0',
            type: 'farming',
            order: 1,
            map_name: 'Teyvat',
            map_match_method: 'TemplateMatch',
        },
        positions: positions,
    };
}

async function teleport(x, y) {
    log.info('传送 → ' + x + ',' + y);
    await call(genshin, ['Tp', 'tp'], x, y);
    await sleep(3500);
}

async function goTo(x, y) {
    const pos = readPos();
    if (!pos) throw new Error('读不到当前坐标，请确认回到主界面');
    const dist = Math.hypot(x - pos.x, y - pos.y);
    if (dist <= 3) return true;

    if (dist > CFG.maxGotoMeters) {
        // 直线追踪不是寻路，超过这个距离必然撞地形，先传送
        log.info('目标 ' + Math.round(dist) + 'm 超过直线上限 ' + CFG.maxGotoMeters + 'm，改走传送');
        await teleport(x, y);
        return true;
    }

    const route = buildRoute(pos, x, y, dist);
    log.info('直线纠偏 ' + Math.round(dist) + 'm（' + route.positions.length + ' 点）');
    const ok = await call(pathingScript, ['Run', 'run'], JSON.stringify(route));
    return ok !== false;
}

function interact(texts) {
    let screen = null;
    try {
        screen = captureGameRegion();
        const list = (texts && texts.length) ? texts : ['调查', '对话', '拾取', '开启'];
        const ok = call(Bv, ['FindFAndPress', 'findFAndPress'], screen, list);
        return !!ok;
    } catch (e) {
        log.warn('交互失败：' + e.message);
        return false;
    } finally {
        release(screen);
    }
}

// ---------- 规则决策 ----------
let planIndex = 0;

function loadProgress() {
    if (!CFG.saveProgress) return;
    try {
        const p = JSON.parse(pick(file, ['ReadTextSync', 'readTextSync'])('progress.json'));
        if (p && typeof p.planIndex === 'number' && p.planIndex <= CFG.plan.length) {
            planIndex = p.planIndex;
            log.info('从进度恢复：第 ' + (planIndex + 1) + ' 个目标');
        }
    } catch (e) { }
}

function saveProgress() {
    if (!CFG.saveProgress) return;
    try {
        pick(file, ['WriteTextSync', 'writeTextSync'])('progress.json', JSON.stringify({ planIndex: planIndex, at: new Date().toISOString() }));
    } catch (e) { }
}

function ruleStep(s) {
    const item = CFG.plan[planIndex];
    if (!item) return { action: 'done', why: '计划已全部完成' };
    if (!s.pos) return { action: 'wait', seconds: 2, why: '等待主界面' };

    const dist = Math.hypot(item.x - s.pos.x, item.y - s.pos.y);
    if (dist <= CFG.arriveRadius) {
        if (item.interact && item.interact.length) {
            return { action: 'interact', texts: item.interact, why: '到达 ' + item.desc };
        }
        planIndex++;
        saveProgress();
        return { action: 'wait', seconds: 0.5, why: '已到达 ' + item.desc };
    }
    return { action: 'goto', x: item.x, y: item.y, why: item.desc };
}

// ---------- AI 决策 ----------
const SYSTEM_PROMPT = [
    '你是原神自动化脚本的决策模块，只能从下列动作中选一个，输出单个 JSON，不要任何解释。',
    '{"action":"goto","x":数字,"y":数字,"why":"原因"} 走向世界坐标，仅 150 米内有效',
    '{"action":"tp","x":数字,"y":数字,"why":"原因"} 传送到该坐标附近',
    '{"action":"interact","texts":["对话"],"why":"原因"} 按 F 与提示文字匹配的物体交互',
    '{"action":"wait","seconds":数字,"why":"原因"} 等待过场或加载',
    '{"action":"done","why":"原因"} 目标已达成',
    '{"action":"fail","why":"原因"} 无法继续',
    '坐标与状态里的 pos 同一坐标系（提瓦特世界坐标）。不要输出 JSON 以外的内容。',
].join('\n');

let aiCalls = 0;
const history = [];

function parseAction(text) {
    if (!text) throw new Error('模型返回为空');
    let t = String(text).trim();
    const fence = t.match(/```(?:json)?\s*([\s\S]*?)```/);
    if (fence) t = fence[1].trim();
    const a = t.indexOf('{');
    const b = t.lastIndexOf('}');
    if (a >= 0 && b > a) t = t.slice(a, b + 1);

    const obj = JSON.parse(t);
    const allow = ['goto', 'tp', 'interact', 'wait', 'done', 'fail'];
    if (allow.indexOf(obj.action) < 0) throw new Error('非法动作：' + obj.action);
    return obj;
}

// 多模态分支：只在 vision.mode = multimodal 时存图，路径相对 BetterGI 安装目录，
// 由本地代理读文件转 base64，脚本侧不碰图片内容，也就不占内存带宽。
function saveShot() {
    if (!HAS.ocr) return null;
    if (!CFG.vision || CFG.vision.mode !== 'multimodal') return null;
    try {
        const p = 'debug/state.png';
        const ok = call(ocr, ['SaveRegion', 'saveRegion'], p, 0, 0, 0, 0);
        return ok ? p : null;
    } catch (e) {
        return null;
    }
}

async function askAI(s) {
    const state = Object.assign({}, s);
    const shot = saveShot();
    if (shot) state.shot = shot;      // 代理看到这个字段就去读图，没配多模态就没有

    const userContent = JSON.stringify({
        goal: CFG.goal,
        target: CFG.plan[planIndex] || null,
        state: state,
        recent: history.slice(-3),
    });

    const limit = HAS_AI ? (ai.MaxCalls || CFG.ai.maxCalls) : CFG.ai.maxCalls;
    if (aiCalls >= limit) throw new Error('已达到单次运行 AI 调用上限 ' + limit);
    aiCalls++;

    // 首选：BGI 自带的 ai 宿主。密钥不出客户端，也不需要 JS HTTP 权限与白名单。
    if (HAS_AI) {
        if (!ai.Enabled) throw new Error('设置页「AI 代理（脚本）」未启用');
        const text = await ai.Chat(userContent, SYSTEM_PROMPT);
        return parseAction(text);
    }

    // 退化：没有特别版宿主时自己发请求，需要 HTTP 权限 + manifest 白名单
    const payload = {
        model: CFG.ai.model,
        temperature: CFG.ai.temperature,
        messages: [
            { role: 'system', content: SYSTEM_PROMPT },
            { role: 'user', content: userContent },
        ],
    };

    const headers = { 'Content-Type': 'application/json' };
    if (CFG.ai.apiKey) headers['Authorization'] = 'Bearer ' + CFG.ai.apiKey;

    const resp = await call(http, ['Request', 'request'], 'POST', CFG.ai.endpoint, JSON.stringify(payload), JSON.stringify(headers));
    const code = pick(resp, ['status_code', 'statusCode']);
    const body = pick(resp, ['body', 'Body']);
    if (code !== 200) throw new Error('HTTP ' + code + ' ' + String(body).slice(0, 200));

    const json = JSON.parse(body);
    return parseAction(json.choices[0].message.content);
}

// ---------- 主循环 ----------
async function run() {
    // 装了特别版就看设置页的开关，否则看 config.json
    const useAI = HAS_AI
        ? !!ai.Enabled
        : !!(CFG.ai.enabled && CFG.ai.endpoint && CFG.ai.model);
    const modelName = HAS_AI ? (ai.Model || '(未配置)') : (CFG.ai.model || '(未配置)');

    log.info('===== AI 代理启动 =====');
    log.info('决策：' + (useAI ? 'AI（' + modelName + '）' : '纯规则'));
    log.info('通道：' + (HAS_AI ? 'BGI ai 宿主（密钥不出客户端）' : (CFG.ai.endpoint ? '脚本 HTTP 直连' : '无')));
    log.info('感知：' + (HAS.bv ? 'Bv ' : '') + (HAS.ocr ? 'ocr ' : '') + (HAS.quest ? 'quest' : '(基础)'));
    log.info('目标：' + (CFG.goal || '(未设置)'));

    loadProgress();

    let fails = 0;
    let repeat = 0;
    let lastActKey = '';
    let lastPosKey = '';
    for (let step = 0; step < CFG.maxSteps; step++) {
        const s = snapshot();
        log.info(
            '[' + (step + 1) + '/' + CFG.maxSteps + '] ' +
            (s.pos ? Math.round(s.pos.x) + ',' + Math.round(s.pos.y) : '坐标未知') +
            ' | ' + s.ui + (s.stuck ? ' | 疑似卡住' : '') +
            (s.hint ? ' | ' + s.hint.slice(0, 20) : '')
        );

        let act;
        if (useAI) {
            try {
                act = await askAI(s);
                log.info('AI 决策：' + JSON.stringify(act));
            } catch (e) {
                log.warn('AI 不可用，本步退回规则：' + e.message);
                act = ruleStep(s);
            }
        } else {
            act = ruleStep(s);
        }

        history.push({ step: step + 1, action: act.action, why: act.why || '' });

        // 模型原地打转的保护：同一个动作 + 位置没变，连续 4 次就跳过当前目标
        const posKey = s.pos ? Math.round(s.pos.x) + ',' + Math.round(s.pos.y) : '-';
        const actKey = act.action + '|' + val(act.x) + '|' + val(act.y) + '|' + val(act.seconds);
        if (actKey === lastActKey && posKey === lastPosKey) {
            repeat++;
        } else {
            repeat = 0;
            lastActKey = actKey;
            lastPosKey = posKey;
        }
        if (repeat >= 4) {
            log.warn('连续 4 次相同动作且位置未变，判定为原地打转，结束本次运行');
            break;
        }

        if (act.action === 'done') {
            log.info('目标达成：' + (act.why || ''));
            break;
        }
        if (act.action === 'fail') {
            log.error('AI 判定无法继续：' + (act.why || ''));
            break;
        }

        let ok = false;
        try {
            if (act.action === 'goto') ok = await goTo(act.x, act.y);
            else if (act.action === 'tp') { await teleport(act.x, act.y); ok = true; }
            else if (act.action === 'interact') {
                ok = interact(act.texts);
                if (ok) { planIndex++; saveProgress(); }
            } else if (act.action === 'wait') {
                await sleep(Math.max(300, (act.seconds || 1) * 1000));
                ok = true;
            } else {
                log.warn('未知动作：' + act.action);
            }
        } catch (e) {
            log.warn('执行失败：' + e.message);
        }

        fails = ok ? 0 : fails + 1;
        if (fails >= 3) {
            log.warn('连续 3 次执行失败，跳过当前目标');
            planIndex++;
            saveProgress();
            fails = 0;
        }

        await sleep(250);
    }

    log.info('===== 结束（AI 调用 ' + aiCalls + ' 次）=====');
    if (typeof notification !== 'undefined') {
        try { notification.Send('AI 代理', '任务结束，AI 调用 ' + aiCalls + ' 次'); } catch (e) { }
    }
}

(async function () {
    try {
        await run();
    } catch (e) {
        log.error('代理异常退出：' + (e && e.stack ? e.stack : e));
    }
})();
