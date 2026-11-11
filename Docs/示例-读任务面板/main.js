// 读任务追踪面板 —— BetterGI 特别版示例
//
// 需要特别版：原版没有 ocr / quest 宿主对象，会报 "quest is not defined"
// 把整个文件夹放进 User\JsScript\，在脚本列表运行，日志里会打印识别结果

const CONFIG = {
    // 面板区域（1080P 基准），识别不准时改这里，或先用 ocr.saveRegion 存图确认
    panel: { x: 28, y: 118, w: 430, h: 190 },
    saveDebugImage: true,     // 存一张 debug/panel.png 方便调区域
};

async function main() {
    if (CONFIG.saveDebugImage) {
        try {
            ocr.saveRegion('debug/panel.png', CONFIG.panel.x, CONFIG.panel.y, CONFIG.panel.w, CONFIG.panel.h);
            log.info('区域截图已保存到 debug/panel.png');
        } catch (e) {
            log.warn('保存截图失败：' + e.message);
        }
    }

    const t = quest.tracker(CONFIG.panel.x, CONFIG.panel.y, CONFIG.panel.w, CONFIG.panel.h);

    log.info('=== 任务追踪 ===');
    log.info('标题：' + (t.title || '（未识别到）'));
    log.info('步骤：' + (t.step || '（未识别到）'));
    log.info('识别行数：' + t.lines.length);

    for (const line of t.lines) {
        log.info(`  「${line.text}」 x=${Math.round(line.x)} y=${Math.round(line.y)} h=${Math.round(line.height)} 置信度=${line.score.toFixed(2)}`);
    }

    if (!t.title) {
        log.warn('没读到标题。先看 debug/panel.png 里有没有框对位置，再调整 CONFIG.panel');
    }
}

(async () => {
    try {
        await main();
    } catch (e) {
        log.error('执行失败：' + e.message);
    }
})();
