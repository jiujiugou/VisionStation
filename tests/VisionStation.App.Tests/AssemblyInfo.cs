using Xunit;

// WeakReferenceMessenger 是**进程级静态**单例：同一次测试运行里所有存活的 VM 都会收到
// 同一条消息。并行执行时，"我这个实例收到了几条"就无法判定 —— 会看到别的测试
// 发的消息也算在自己头上（表现为计数器多 1、结果行被别的帧覆盖）。
//
// 关掉并行而不是给每个测试造独立 messenger：产品代码里就是全局一个 messenger，
// 测试跟着它的真实语义走，比在测试里造一个"只属于本用例"的假世界更可信。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
