# SB-E3 测试已就绪

E2 已通过 Codex 独立验收。Codex 将两份草稿移入 presentation/src/test/kotlin/com/majiang/screen：SleeveBloomChoiceModelContractTest 6项、SleeveBloomScreenContractTest 8项，共14项（含初检新增4项反馈提示回归）。测试是只读契约，由Anti实现生产代码。

覆盖权威快照开关、无乐观扣费、倒计时/暂停、身份变化、断线与控制权恢复、共享pending、双击/ESC/模态拦截、乱序回执、真实按钮命中与分离、文案字库、资源释放。最新TASK-ANTI-E3.md与VISUAL-DESIGN.md规定理牌y50/高44、托管y6/高36，覆盖PREP文件较早的托管y50建议。

Anti 运行presentation测试、全仓test、桌面classes和Android调试构建。Codex另用 tools/architecture/SleeveBloomRenderSmoke.java 做1280与960真实GL和生产会话验证。测试夹具错误请给证据，由Codex修正。禁止改测试或以假接口绕过。
