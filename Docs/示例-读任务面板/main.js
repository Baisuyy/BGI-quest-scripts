// 特别版 API 自检：确认 ocr / quest 宿主可用，并打印识别结果。
// 用法：整个文件夹放进 User\JsScript，在脚本列表里运行，看日志输出。

(async function () {
    log.info('===== 特别版 API 自检 =====');

    if (typeof ocr === 'undefined' || typeof quest === 'undefined') {
        log.error('未检测到 ocr / quest，宿主不是特别版或版本不匹配');
        return;
    }

    log.info(`截图尺寸：${ocr.width} × ${ocr.height}，缩放比 ${ocr.scaleTo1080P.toFixed(3)}`);

    // 1. 任务追踪面板
    const t = quest.tracker();
    log.info(`任务标题：${t.title || '(空，可能没开追踪或不在任务中)'}`);
    log.info(`当前步骤：${t.step || '(空)'}`);

    // 2. 面板原始文本行，按行打印坐标，方便核对识别区域
    const lines = ocr.readLines(28, 118, 430, 190);
    log.info(`面板识别到 ${lines.length} 行：`);
    for (const line of lines) {
        log.info(`  [${Math.round(line.x)},${Math.round(line.y)}] ${line.text}  (${line.score.toFixed(2)})`);
    }

    // 3. 屏幕中央的交互提示（F 键、拾取等）
    const hint = ocr.readRegion(760, 620, 400, 80);
    log.info(`中央提示：${hint || '(空)'}`);

    // 4. 存一张面板截图，识别不准时用它调坐标
    if (ocr.saveRegion('debug/quest-panel.png', 28, 118, 430, 190)) {
        log.info('面板截图已保存到脚本目录 debug/quest-panel.png');
    }

    log.info('===== 自检结束 =====');
})();
