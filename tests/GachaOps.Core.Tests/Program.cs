using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GachaOps.Core.Abstractions;
using GachaOps.Core.Adapters;
using GachaOps.Core.Models;
using GachaOps.Core.Services;

Console.OutputEncoding = Encoding.UTF8;
if (args.Contains("--bf01-close-window-helper", StringComparer.Ordinal))
{
    Environment.ExitCode = CloseWindowProcessHelper.Run();
    return;
}

const string OldResourceVersion = MaaResourceTestData.OldVersion;
const string NewResourceVersion = MaaResourceTestData.NewVersion;
const string NewerResourceVersion = MaaResourceTestData.NewerVersion;
var tests = new (string Name, Func<Task> Run)[]
{
    ("每日定时设置与任意分钟选择", ScheduledLaunchTests.SettingsAsync),
    ("定时窄窗口及跨日提醒", ScheduledLaunchTests.TimingAsync),
    ("定时持久认领防重与时钟回拨", ScheduledLaunchTests.ClaimsAsync),
    ("锁屏前台及忙碌准入", ScheduledLaunchTests.DesktopAsync),
    ("任务计划程序安全定义及路径迁移", ScheduledLaunchTests.TaskXmlAsync),
    ("隔离计划任务替身所有权与通知组合", ScheduledLaunchTests.RegistrationAsync),
    ("定时请求向已有实例转交", ScheduledLaunchTests.PipeAsync),
    ("定时跳过历史通知与保存失败", ScheduledLaunchTests.SkipReportAsync),
    ("Bark 设置兼容旧文件并保留独立子选择", NotificationSettingsRoundTripAsync),
    ("通知总开关及子开关均阻止请求", NotificationSwitchesGateRequestsAsync),
    ("自建 Bark 前缀与设备密钥使用 JSON 提交", BarkCustomEndpointAsync),
    ("Bark 失败与响应原文均不泄漏密钥", BarkFailuresAreSanitizedAsync),
    ("Bark 超时包含响应正文且取消有界", BarkTimeoutAndCancellationAsync),
    ("通知结果日志脱敏及写入失败隔离", NotificationJournalAsync),
    ("整轮汇总隔离身份并保留异常和日志", WorkflowSummaryIncludesFailuresAsync),
    ("整轮成功与缺失任务历史失败准确区分", WorkflowSummarySuccessPolicyAsync),
    ("联网失败后安装不安全时不启动其他更新", UnsafeInstallationBlocksOtherUpdatesAsync),
    ("任一预检失败立即阻止整轮准备", PreparationFailureBlocksWholeWorkflowAsync),
    ("未勾选的三个工具均能提前阻止准备", UnselectedRunningToolBlocksPreparationAsync),
    ("启动前发现被排除工具仍运行时整轮不启动", RunningExcludedToolBlocksPreparedWorkflowAsync),
    ("准备后才出现的工具进程阻止整轮且下次运行重查", RunningToolAfterPreparationBlocksAndResetsAsync),
    ("未参与本轮的工具进程也阻止启动", UnplannedRunningToolBlocksAsync),
    ("工具退出后重新准备可运行且不重复检查更新", ExitedExcludedToolDoesNotBlockAsync),
    ("只选两个工具及全部取消能够持久化", SelectedToolsPersistAsync),
    ("首次使用不自动添加工具", NewSettingsStartWithoutToolsAsync),
    ("修正未通过预检的工具后仍可首次检查", SessionPreflightFailureDoesNotConsumeCheckAsync),
    ("取消不会消耗尚未开始工具的检查机会", SessionCancellationKeepsUnstartedChecksAsync),
    ("已检查工具恢复失败阻止整轮且保留检查机会", SessionFailedRecoveryRemainsBlockedAsync),
    ("更新警告不改变成功记录且阻止自动退出", UpdateWarningsKeepWindowAfterSuccessfulRunAsync),
    ("同次打开反复运行只检查每个工具一次", SessionChecksEachToolOnceAsync),
    ("更改启用工具时只检查未检查过的工具", SessionChecksOnlyNewToolsAsync),
    ("检查失败或取消后不重查且保留本地安全检查", SessionFailedOrCancelledCheckIsNotRetriedAsync),
    ("最终提醒不展示底层长篇异常", CompletionReminderIsConciseAsync),
    ("更新预检拒绝已运行的官方入口", UpdatePreflightRejectsRunningEntryAsync),
    ("版本检查超时覆盖响应正文", ReleaseCheckTimeoutIncludesBodyAsync),
    ("已有工具或缺少路径的生产失败保守结束", ExistingProductionToolBlocksContinuationAsync),
    ("失败订阅异常后清理并隔离下一轮身份", FailureEventCleanupAllowsNextWorkflowAsync),
    ("未启动且无工具残留的生产失败自动继续", UnstartedProductionFailureContinuesAsync),
    ("失败超时结束本通道且跳过记录恰好一次", UnsafeFailureRecordsSkippedOnceAsync),
    ("新设置和缺失设置文件的程序路径为空", ToolPathsDefaultToEmptyAsync),
    ("损坏设置文件会保留原文并返回一次恢复标记", CorruptSettingsIsPreservedAndReportedOnceAsync),
    ("损坏设置恢复后再次加载不重复报告", CorruptSettingsRecoveryIsNotReportedTwiceAsync),
    ("结构无效设置会逐种保留并且只报告一次", StructurallyInvalidSettingsAreRecoveredOnceAsync),
    ("兼容设置形态不会被误判为损坏", CompatibleSettingsShapesContinueToLoadAsync),
    ("设置文件临时锁定会抛出读取异常且保留原文件", LockedSettingsFileThrowsWithoutFallbackAsync),
    ("显式保存的程序路径能够原样往返", ToolPathsRoundTripAsync),
    ("启动与结束设置使用兼容默认值", StartupAutomationSettingsDefaultToDisabledAsync),
    ("旧设置缺少启动与结束字段仍可加载", LegacySettingsWithoutStartupAutomationLoadAsync),
    ("启动与结束设置能够往返保存", StartupAutomationSettingsRoundTripAsync),
    ("关闭自动更新会跳过联网检查", DisabledToolUpdateSkipsProvidersAsync),
    ("工具更新按完整版本规则兼容测试版", ToolUpdateProviderComparesPrereleaseVersionsAsync),
    ("无法识别工具版本时跳过更新并继续准备", UnrecognizedToolVersionWarnsAndContinuesAsync),
    ("更新活动会报告检查阶段和实际更新项目", ToolUpdateActivityReportsStagesAndItemsAsync),
    ("工具路径配置或实例变化只重新本地预检", SessionToolSettingsDoNotRepeatChecksAsync),
    ("开关自动更新不会重复检查已检查的工具", SessionUpdateToggleDoesNotRepeatChecksAsync),
    ("新的应用会话会重新检查工具", NewCoordinatorChecksToolsAgainAsync),
    ("跳过重复检查时仍优先恢复未完成更新", SessionPendingUpdateStillRecoversAsync),
    ("同一协调器并发准备会快速拒绝且不触碰活跃更新", ConcurrentPreparationFailsFastAsync),
    ("版本检查屏障结束后跳过失败检查并继续可用更新", ToolUpdateCheckBarrierAsync),
    ("官方更新会并行执行", ToolUpdatesRunInParallelAsync),
    ("取消仍等待不可中断的本地写入安全收尾", ToolUpdateCancellationWaitsForStartedUpdatesAsync),
    ("BetterGI 更新和恢复等待可取消且保留进程", () => ExternalUpdateCancellationAsync(ToolId.BetterGi, false)),
    ("MAA 更新和恢复等待可取消且保留进程", () => ExternalUpdateCancellationAsync(ToolId.Maa, true)),
    ("MaaEnd 更新和恢复等待可取消且保留进程", () => ExternalUpdateCancellationAsync(ToolId.MaaEnd, true)),
    ("更新结果保存失败阻止整轮并保留恢复点", UpdateStateSaveFailureRetainsEvidenceAsync),
    ("不确定安装阻止整轮并保留任务身份", UncertainInstallationsAreExcludedAsync),
    ("全部准备失败仍有完整历史和一次结束提醒", AllPreparationFailuresRetainHistoryAsync),
    ("准备取消保留先前失败和恢复证据", PreparationCancellationRetainsFailuresAsync),
    ("可运行任务沿用规范化去重顺序", RunnablePreparationUsesSnapshotAsync),
    ("空恢复集合不能伪装成安全状态", NullPendingUpdatesBlocksPreparationAsync),
    ("安全更新失败回退旧安装且不伪造任务失败", SafeUpdateFailureFallsBackAsync),
    ("恢复失败后停止其他工具准备", RecoveryFailureIsolatedAsync),
    ("未完成整轮提醒保留准备原因和历史保护", IncompleteWorkflowReminderAsync),
    ("更新失败会等待其他工具并写入历史", ToolUpdateFailureWaitsForSiblingsAsync),
    ("未完成更新会在新运行前恢复", PendingToolUpdateRecoversBeforeChecksAsync),
    ("旧可信指纹不会阻止同版本文件变化", LegacyTrustedFingerprintDoesNotBlockPreparationAsync),
    ("更新后二次预检失败会阻止工作流", PostUpdatePreflightFailureBlocksAsync),
    ("三个更新 provider 使用限定启动参数", ToolUpdateProvidersUseExpectedArgumentsAsync),
    ("正常更新完成后应正常关闭自更新程序", ToolUpdateClosesProcessAfterUpdateAsync),
    ("更新恢复应正常关闭自更新程序进程", ToolUpdateRecoveryClosesProcessAsync),
    ("更新恢复等待完整指纹连续稳定", ToolUpdateRecoveryWaitsForStableFingerprintAsync),
    ("更新恢复等待自更新进程完成后再接受稳定指纹", ToolUpdateRecoveryWaitsForUpdaterExitAfterQuietFingerprintAsync),
    ("更新恢复等待外部更新器完成后再接受稳定指纹", ToolUpdateRecoveryWaitsForWorkerExitAfterQuietFingerprintAsync),
    ("自更新后由更新器拉起的新进程应被正常关闭", ToolUpdateClosesProcessRestartedByWorkerAsync),
    ("更新恢复后由外部更新器拉起的新进程应被正常关闭", ToolUpdateRecoveryClosesProcessRestartedByWorkerAsync),
    ("MaaEnd 更新恢复会重试暂缺版本的接口文件", MaaEndRecoveryRetriesIncompleteInterfaceWhileUpdaterRunsAsync),

    ("BetterGI 更新恢复拒绝跨安装路径", BetterGiRecoveryRejectsDifferentInstallationAsync),
    ("MaaEnd 更新恢复拒绝跨安装路径", MaaEndRecoveryRejectsDifferentInstallationAsync),
    ("同一安装的规范化路径不会误阻止恢复", NormalizedInstallationPathDoesNotBlockRecoveryAsync),
    ("旧更新状态缺少安装身份时停止自动恢复", LegacyUpdateStateWithoutInstallationFailsClosedAsync),
    ("新更新事务保存规范化安装身份", ToolUpdatePendingPersistsInstallationPathAsync),
    ("MAA 测试版更新通道不会被启动准备拒绝", MaaBetaUpdateChannelIsAllowedAsync),
    ("MaaEnd 测试版更新通道不会被启动准备拒绝", MaaEndBetaUpdateChannelIsAllowedAsync),
    ("MAA 未启用时不检查资源", DisabledMaaSkipsResourceChecksAsync),
    ("MAA 资源路径支持空格中文和规范化绝对路径", MaaResourcePathsAreNormalizedAsync),
    ("MAA 资源检查不读取无关资源文件", MaaResourceCheckUsesLightweightFingerprintAsync),
    ("仅 MAA 资源落后时可发现并静默部署", MaaResourceOnlyUpdateIsDetectedAndAppliedAsync),
    ("仅 MAA 资源落后时不调用程序更新入口", MaaResourceOnlyUpdateSkipsProgramUpdaterAsync),
    ("MAA 程序与资源同时更新时严格顺序执行", MaaProgramAndResourceUpdatesAreSequencedAsync),
    ("MAA 资源计划固定到检查时的确切 commit", MaaResourcePlanPinsExactCommitAsync),
    ("MAA 资源检查后只拒绝关键状态变化", MaaResourcePlanOnlyRejectsRelevantStateChangesAsync),
    ("MAA 资源已是目标 commit 时不重复下载", MaaResourceAtTargetCommitSkipsDownloadAsync),
    ("MAA 资源第二次更新只备份和写入变化文件", MaaResourceSecondUpdateOnlyTouchesChangedFilesAsync),
    ("MAA 资源纯 commit 更新不写入资源文件", MaaResourceCommitOnlyUpdateWritesNoResourceFilesAsync),
    ("MAA 资源网络检查和下载只重试一次", MaaResourceNetworkRetriesOnceAsync),
    ("MAA 资源下载取消不会写入 resource", MaaResourceDownloadCancellationDoesNotWriteAsync),
    ("MAA 资源归档拒绝路径攻击和无效版本", MaaResourceArchiveValidationRejectsUnsafeInputsAsync),
    ("MAA 资源更新保留自定义文件并覆盖官方文件", MaaResourceUpdatePreservesCustomFilesAsync),
    ("MAA 资源部署后完整清单和哈希异常会回滚", MaaResourcePostWriteVerificationRollsBackAsync),
    ("MAA 资源各写入阶段失败均完整回滚", MaaResourceWriteStageFailuresRollBackAsync),
    ("MAA 资源备份后崩溃及旧阶段均可恢复", MaaResourcePreparedCrashRecoversAsync),
    ("MAA 资源安装状态兼容旧指纹字段", MaaResourceInstallationStateFingerprintCompatibilityAsync),
    ("MAA 资源模拟崩溃后由事务日志恢复", MaaResourceCrashRecoveryRollsBackAsync),
    ("MAA 资源提交后模拟崩溃会恢复已提交事务", MaaResourceCommittedCrashRecoversAsync),
    ("MAA 资源回滚失败保留证据并要求恢复", MaaResourceRollbackFailurePreservesEvidenceAsync),
    ("MAA 资源事务路径被篡改为根目录时拒绝清理", MaaResourceTamperedRootPathBlocksRecoveryAsync),
    ("MAA 资源写入开始后取消会等待安全提交", MaaResourceCancellationWaitsAfterWriteStartsAsync),
    ("MAA 资源状态按安装路径隔离且保留旧快照", MaaResourceStateIsIsolatedAndPreservesLegacySnapshotAsync),
    ("MAA 资源临时快照失败不会影响旧快照", MaaResourceTemporarySnapshotFailurePreservesLegacySnapshotAsync),
    ("同会话资源变化不会重复联网检查", MaaResourceChangesDoNotRepeatSessionCheckAsync),
    ("旧 MAA pending 缺少安装身份时停止自动恢复", LegacyMaaPendingWithoutInstallationIdentityFailsClosedAsync),
    ("MAA pending 路径变化会阻止跨安装恢复", MaaPendingPathChangeBlocksRecoveryAsync),
    ("旧工具更新状态缺少 provider 数据仍可读取", LegacyToolUpdateStateWithoutProviderDataLoadsAsync),
    ("工具更新状态路径是目录时不能伪装成不存在", ToolUpdateStateDirectoryPathThrowsAsync),
    ("工具更新状态文件不存在时返回空状态", MissingToolUpdateStateReturnsEmptyAsync),
    ("损坏工具更新状态保持独立解析错误", CorruptToolUpdateStateReportsInvalidDataAsync),
    ("工具更新状态访问错误会阻断准备", ToolUpdateStateAccessErrorBlocksPreparationAsync),
    ("自动退出只在满足条件时触发", WorkflowAutomationPolicyMatchesExitLifecycleAsync),
    ("执行异常完整结束的退出与提醒策略正确", WorkflowAutomationPolicyMatchesCompletionErrorNotificationAsync),
    ("旧 QueueOrder 迁移为通道 1 工作流", LegacyQueueOrderMigratesAsync),
    ("工作流规范化并持久化", WorkflowNormalizesAndPersistsAsync),
    ("旧通道 3 JSON 保留顺序和启停并回落到通道 1", LegacyThirdChannelMigratesAsync),
    ("工作流快照保持全局顺序、启用和通道", WorkflowSnapshotPreservesUiMappingAsync),
    ("任务可在通道内排序并拖入其他通道", WorkflowMovesWithinAndAcrossChannelsAsync),
    ("同通道严格串行", SameChannelRunsSeriallyAsync),
    ("不同通道并行", DifferentChannelsRunInParallelAsync),
    ("通道同步启动前缀不会阻塞其他通道", SynchronousChannelStartDoesNotBlockOtherChannelsAsync),
    ("通道完成当前项后立即补位", ChannelStartsNextWithoutWaitingAsync),
    ("执行异常保存记录并继续当前通道", CompletedWithErrorsContinuesChannelAsync),
    ("全部任务纯成功计为所有计划任务完成", SuccessfulQueueReportsAllPlannedTasksCompletedAsync),
    ("执行异常完整结束返回独立队列结果", CompletedWithErrorsQueueReportsDistinctResultAsync),
    ("单任务失败后结束通道计为未全部完成", SingleFailureEndChannelReportsIncompleteAsync),
    ("多通道部分结束计为未全部完成", PartialChannelEndReportsIncompleteAsync),
    ("停止后续计为未全部完成", StopAfterCurrentReportsIncompleteAsync),
    ("取消任务计为未全部完成", CancelledTaskReportsIncompleteAsync),
    ("取消后返回失败不会启动下一项", CancelledFailedResultDoesNotAdvanceQueueAsync),
    ("取消后返回成功不会启动下一项", CancelledSucceededResultDoesNotAdvanceQueueAsync),
    ("没有实际计划任务计为未全部完成", EmptyQueueReportsIncompleteAsync),
    ("Win32 启动异常保留原因且不影响其他通道", Win32StartFailurePausesOnlyItsChannelAsync),
    ("停止后续会阻止所有通道未启动任务", StopSkipsAllFutureTasksAsync),
    ("停止与下一项启动竞争遵守原子边界", StopAndNextStartRaceIsAtomicAsync),
    ("新一轮队列会重置停止请求", StopRequestResetsForNextRunAsync),
    ("崩溃写入生成包含最小字段的合法 JSONL", CrashLogWriteCreatesValidJsonLineAsync),
    ("崩溃消息和堆栈遵守固定长度上限", CrashLogFieldsRespectLengthLimitsAsync),
    ("崩溃清理移除八天前记录并保留有效记录", CrashLogPruneKeepsSevenDaysAsync),
    ("崩溃损坏尾行不阻止后续写入", CorruptCrashLogTailDoesNotBlockWriteAsync),
    ("崩溃记录路径不可写时安全失败", CrashLogWriteFailsSafelyForUnwritablePathAsync),
    ("崩溃并发写入保持每行 JSON 完整", ConcurrentCrashLogWritesRemainValidAsync),
    ("历史追加不保存原始日志且不改变内存记录", HistoryAppendOmitsRawLogExcerptWithoutMutatingInputAsync),
    ("旧历史原始日志在首次读取时清除", LegacyHistoryRawLogIsRemovedOnFirstReadAsync),
    ("历史读取过滤过期记录并压缩文件", HistoryReadFiltersExpiredRecordsAndCompactsFileAsync),
    ("历史追加清理过期记录并完整保留新旧有效记录", HistoryAppendCleansExpiredRecordsAndPreservesValidRecordsAsync),
    ("历史保留恰好七天并过滤更早一瞬间的记录", HistoryRetentionKeepsExactSevenDayBoundaryAsync),
    ("历史追加拒绝新传入的过期记录", HistoryAppendRejectsExpiredIncomingRecordAsync),
    ("旧历史 JSONL 继续兼容", LegacyHistoryRemainsReadableAsync),
    ("历史残缺尾行后追加不会吞掉新记录", HistoryAppendAfterCorruptTailPreservesNewRecordAsync),
    ("历史 JSON null 行会被判为无效并移除", NullHistoryRowIsRemovedAsync),
    ("历史路径为目录时不能伪装成空历史", HistoryDirectoryPathThrowsAsync),
    ("历史损坏行和残缺尾行不影响有效记录", CorruptHistoryRowsDoNotAffectValidRecordsAsync),
    ("历史并发追加不会互相覆盖", ConcurrentHistoryAppendsDoNotOverwriteAsync),
    ("执行异常历史使用字符串枚举往返", CompletedWithErrorsHistoryRoundTripAsync),
    ("历史读取上限只保留最新记录", HistoryLatestLimitKeepsNewestRecordsAsync),
    ("BetterGI 一条龙等待整轮结束标记", BetterGiOneDragonWaitsForGlobalCompletionAsync),
    ("BetterGI 单个配置组结束后完成", BetterGiSingleScriptGroupCompletesAsync),
    ("BetterGI 多配置组等待全部结束且顺序无关", BetterGiMultipleScriptGroupsWaitForAllAsync),
    ("BetterGI 配置组忽略重复无关和相似名称", BetterGiScriptGroupsRequireExactDistinctMatchesAsync),
    ("BetterGI 配置组内部异常等待可靠结束标记", BetterGiScriptGroupInternalErrorCompletesWithErrorsAsync),
    ("BetterGI 没有可靠结束标记仍判失败", BetterGiWithoutReliableCompletionFailsAsync),
    ("BetterGI 配置组旧日志检查点不参与本轮", BetterGiScriptGroupCheckpointIgnoresOldCompletionsAsync),
    ("BetterGI 并行监控的配置组状态相互隔离", BetterGiScriptGroupMonitorRunsAreIsolatedAsync),
    ("BetterGI 区分执行异常和阻断失败", BetterGiCompletionSemanticsAsync),
    ("BetterGI 最终任务错误完整结束记为执行异常", BetterGiTerminalTaskFailureCompletesWithErrorsAsync),
    ("BetterGI 最终任务错误只显示可读步骤", BetterGiTerminalTaskFailureDetailIsReadableAsync),
    ("BetterGI 树脂耗尽正常结束不记为执行异常", BetterGiResinExhaustionRemainsSuccessfulAsync),
    ("BetterGI 传送重试成功不记为执行异常", BetterGiSuccessfulRetryRemainsSuccessfulAsync),
    ("BetterGI 未配置退出自身时完成不要求程序退出", BetterGiCompletionDoesNotRequireProcessExitAsync),
    ("BetterGI 配置退出自身时完成后等待进程退出", BetterGiConfiguredSelfExitWaitsForProcessExitAsync),
    ("MAA 内部错误后完整结束记为执行异常", MaaInternalErrorsCompleteWithErrorsAsync),
    ("MAA 完成与整轮异常标记统一归约", MaaCompletionEvidenceSemanticsAsync),
    ("MAA 配置退出自身时完成后等待进程退出", MaaExitSelfCompletionWaitsForProcessExitAsync),
    ("MAA 未配置退出自身时完成不要求程序退出", MaaCompletionWithoutExitSelfDoesNotRequireProcessExitAsync),
    ("MAA 未完成或异常退出仍判失败", MaaIncompleteOrAbnormalExitFailsAsync),
    ("旧完成日志不会误判", OldSuccessIsIgnoredAsync),
    ("日志轮换后仍能识别完成", RotatedLogIsDetectedAsync),
    ("MaaEnd 子目录运行日志可识别正常完成", MaaEndNestedRuntimeSuccessIsDetectedAsync),
    ("MaaEnd 正常退出前必须完成全部已启动任务", MaaEndIncompleteRunIsRejectedOnExitAsync),
    ("MaaEnd 当前自身停止标记可完成镜像日志运行", MaaEndCurrentSelfStopCompletesMirroredRunAsync),
    ("MaaEnd 日志轮换不会复用旧完成标记", MaaEndRotatedBackupDoesNotReuseOldSuccessAsync),
    ("MaaEnd 日志瞬时轮换仍会补读未消费尾部", MaaEndRotatedBackupReadsUnreadTailAsync),
    ("MaaEnd 配置退出自身时完成后等待进程正常退出", MaaEndCompletionWaitsForNormalExitAsync),
    ("MaaEnd 连接重试恢复及任务异常仍等待正常退出", MaaEndRetryRecoveryWaitsForNormalExitAsync),
    ("MaaEnd 连接重试不替代完成和正常退出证据", MaaEndRetryDoesNotImplyCompletionAsync),
    ("MaaEnd 未配置退出自身时完成不要求程序退出", MaaEndCompletionWithoutSelfExitDoesNotRequireProcessExitAsync),
    ("MaaEnd 应用完成证据不能覆盖异常退出", MaaEndAppCompletionRejectsAbnormalExitAsync),
    ("MaaEnd 失败任务映射为中文用户名称", MaaEndFailedTaskMapsToLocalizedUserNameAsync),
    ("MaaEnd 直接中文任务标签能够解析", MaaEndDirectTaskLabelIsResolvedAsync),
    ("MaaEnd 无效任务元数据安全退化为空明细", MaaEndInvalidTaskMetadataFallsBackWithoutDetailsAsync),
    ("MaaEnd 应用完成后任务失败记为执行异常", MaaEndTaskFailureChangesAppCompletionResultAsync),
    ("MaaEnd 忽略无进程来源的残缺重复事件", MaaEndMalformedDuplicateEventIsIgnoredAsync),
    ("MaaEnd 按规范工作项终态归约退出结果", MaaEndWorkItemExitReductionAsync),
    ("MaaEnd 完成证据不能覆盖阻断失败", MaaEndCompletionEvidencePriorityAsync),
    ("MaaEnd PostStop 不能单独证明整轮完成", MaaEndPostStopAloneIsNotCompletionAsync),
    ("MaaEnd 子目录整轮异常可识别为执行异常", MaaEndNestedRuntimeErrorCompletionIsDetectedAsync),
    ("完成文案由退出策略而非进程退出时序决定", CompletionMessageFollowsFinalizationPolicyAsync),
    ("完成标记后非零退出仍判失败", CompletionThenAbnormalExitFailsAsync),
    ("完成标记后的阻断日志仍优先失败", BlockingFailureAfterCompletionStillFailsAsync),
    ("完成标记后的内部错误归为执行异常", InternalErrorAfterCompletionCompletesWithErrorsAsync),
    ("执行异常明细按首次顺序去重并准确报告溢出", InternalErrorDetailsDeduplicateAndReportOverflowAsync),
    ("等待进程退出时无新日志仍会超时", CompletionFinalizationNoLogTimesOutAsync),
    ("等待进程退出时最长运行仍会超时", CompletionFinalizationHardTimeoutAsync),
    ("等待进程退出时取消保持取消", CompletionFinalizationCancellationIsPreservedAsync),
    ("高频日志保持固定摘录上限", HighVolumeLogKeepsBoundedExcerptAsync),
    ("高频日志不会捕获 UI 同步上下文", LogProgressDoesNotCaptureSynchronizationContextAsync),
    ("超长日志行仍能识别跨块完成标记", LongLogLineDetectsMarkerAcrossChunksAsync),
    ("UTF-8 中文字符跨轮询保持完整", Utf8CharacterSplitAcrossPollsRemainsIntactAsync),
    ("UTF-8 BOM 与无换行尾部保持兼容", Utf8BomAndCarryRemainCompatibleAsync),
    ("UTF-8 解码状态在日志截断后重置", Utf8DecodeStateResetsAfterTruncationAsync),
    ("固定路径替换为更长新文件时读取新前缀", ReplacedLogReadsNewPrefixWhenLengthReachesOldOffsetAsync),
    ("固定路径清空后快速回长时读取新前缀", RegrownLogReadsNewPrefixWhenLengthReachesOldOffsetAsync),
    ("日志持续锁定时超时保留读取失败诊断", PersistentLogReadFailureIsReportedAtNoLogTimeoutAsync),
    ("日志短暂锁定恢复后清除读取失败诊断", TransientLogReadFailureClearsAfterRecoveryAsync),
    ("日志扫描期间取消优先于同批完成标记", LogScanCancellationPrecedesSameBatchCompletionAsync),
    ("无新日志会超时", NoLogTimesOutAsync),
    ("MaaEnd 当前实例自身退出任务不注入兼容退出参数", MaaEndEnabledSelfExitTaskOmitsQuitAfterRunAsync),
    ("MaaEnd 自身退出任务禁用时不注入兼容退出参数", MaaEndDisabledSelfExitTaskOmitsQuitAfterRunAsync),
    ("MaaEnd 自身退出任务不适用当前控制器时不注入兼容退出参数", MaaEndControllerMismatchOmitsQuitAfterRunAsync),
    ("MaaEnd 其他实例自身退出任务不注入当前实例参数", MaaEndOtherInstanceSelfExitTaskOmitsQuitAfterRunAsync),
    ("MaaEnd 配置缺失异常或不完整时不注入兼容退出参数", MaaEndUncertainSelfExitConfigurationOmitsQuitAfterRunAsync),
    ("配置发现区分成功有值和成功为空", ProfileDiscoveryDistinguishesSuccessfulCandidatesAndEmptyAsync),
    ("配置发现把存储缺失和访问错误返回为失败", ProfileDiscoveryReportsMissingAndUnreadableStorageAsync),
    ("配置发现拒绝损坏和错误 JSON 结构", ProfileDiscoveryRejectsInvalidJsonStructuresAsync),
    ("适配器对错误配置返回验证失败而不抛异常", AdapterValidationRejectsInvalidProfileConfigurationAsync),
    ("中文及空格参数保持独立", ArgumentsPreserveChineseAndSpacesAsync),
    ("路径失效会阻止启动", MissingPathIsRejectedAsync),
    ("工具已运行会拒绝重复启动", RunningProcessIsRejectedAsync),
    ("MAA 结束行为由用户决定", MaaPostActionsAreUserConfigurableAsync),
    ("设置与历史能够恢复", SettingsAndHistoryRoundTripAsync),
    ("集中工具目录可创建全部适配器", ToolCatalogCreatesAllAdaptersAsync)
};

if (args.Contains("--cancellation-stress", StringComparer.Ordinal))
{
    var cancellationTests = tests.Where(test =>
        test.Name.Contains("更新和恢复等待可取消且保留进程", StringComparison.Ordinal)).ToArray();
    tests = Enumerable.Range(0, 50).SelectMany(_ => cancellationTests).ToArray();
}

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"[通过] {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"[失败] {test.Name}\n       {exception}");
    }
}

Console.WriteLine($"\n结果：{tests.Length - failures.Count}/{tests.Length} 通过");
if (failures.Count > 0)
{
    Environment.ExitCode = 1;
}

static async Task NotificationSettingsRoundTripAsync()
{
    using var area = TestArea.Create();
    await File.WriteAllTextAsync(area.File("settings.json"), "{}");
    var store = new SettingsStore(area.Root);
    var settings = (await store.LoadAsync()).Settings;
    Assert.False(settings.NotificationsEnabled, "旧设置默认不发送");
    Assert.True(settings.NotifyBeforeScheduledRun && settings.NotifyRunResult, "默认提醒和结果开启");
    Assert.False(settings.NotifyRunStarted, "默认开始通知关闭");
    settings.BarkAddress = " https://bark.invalid/prefix/test-device ";
    settings.NotifyBeforeScheduledRun = false;
    settings.NotifyRunStarted = true;
    await store.SaveAsync(settings);
    var restored = (await store.LoadAsync()).Settings;
    Assert.False(restored.NotificationsEnabled || restored.NotifyBeforeScheduledRun, "总开关关闭保存子选择");
    Assert.True(restored.NotifyRunStarted && restored.NotifyRunResult, "子选择无损");
    Assert.Equal(settings.BarkAddress, restored.BarkAddress, "地址往返");
    Assert.Equal(TimeSpan.FromMinutes(5), BarkNotificationService.ReminderLeadTime, "提前五分钟");
}

static async Task NotificationSwitchesGateRequestsAsync()
{
    var count = 0;
    using var client = new HttpClient(new NotificationTestHandler((_, _) =>
    {
        count++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":200}") });
    }));
    var sender = new BarkNotificationService(client);
    var settings = new AppSettings { BarkAddress = "https://bark.invalid/device" };
    foreach (var kind in Enum.GetValues<RunNotificationKind>())
        Assert.False((await sender.SendAsync(settings, kind, "测试", "内容")).Sent, "总开关关闭无请求");
    Assert.Equal(0, count, "不联网");
    settings.NotificationsEnabled = true;
    await sender.SendAsync(settings, RunNotificationKind.Started, "开始", "内容");
    Assert.Equal(0, count, "开始默认不发送");
    await sender.SendAsync(settings, RunNotificationKind.Reminder, "提醒", "内容");
    await sender.SendAsync(settings, RunNotificationKind.Result, "结果", "内容");
    Assert.Equal(2, count, "通知不依赖自动运行或定时");
    settings.NotifyBeforeScheduledRun = false;
    settings.NotifyRunResult = false;
    await sender.SendAsync(settings, RunNotificationKind.Reminder, "提醒", "内容");
    await sender.SendAsync(settings, RunNotificationKind.Result, "结果", "内容");
    Assert.Equal(2, count, "各子开关关闭生效");
    await sender.SendAsync(settings, RunNotificationKind.Test, "测试", "内容");
    Assert.Equal(3, count, "测试只需总开关");
    settings.NotifyRunStarted = true;
    await sender.SendAsync(settings, RunNotificationKind.Started, "开始", "内容");
    Assert.Equal(4, count, "开始独立启用");
}

static async Task BarkCustomEndpointAsync()
{
    using var client = new HttpClient(new NotificationTestHandler(async (request, token) =>
    {
        Assert.Equal("http://localhost:8080/bark/push", request.RequestUri!.AbsoluteUri, "保留反向代理前缀和端口");
        Assert.Equal(HttpMethod.Post, request.Method, "POST");
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        Assert.Equal("test device", json.RootElement.GetProperty("device_key").GetString(), "解码密钥只在正文中");
        Assert.Equal("中文 & 内容", json.RootElement.GetProperty("body").GetString(), "正文无需路径编码");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":200}") };
    }));
    var result = await new BarkNotificationService(client).SendAsync(new AppSettings
    {
        NotificationsEnabled = true, BarkAddress = "http://localhost:8080/bark/test%20device/"
    }, RunNotificationKind.Result, "结果", "中文 & 内容");
    Assert.True(result.Sent, "自建服务可用");
}

static async Task BarkFailuresAreSanitizedAsync()
{
    const string secret = "private-device-key";
    var settings = new AppSettings { NotificationsEnabled = true, BarkAddress = "https://bark.invalid/" + secret };
    foreach (var content in new[] { "broken " + secret, "{\"code\":400,\"message\":\"" + secret + "\"}", "[]", "{\"code\":\"200\"}" })
    {
        using var client = new HttpClient(new NotificationTestHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) })));
        var result = await new BarkNotificationService(client).SendAsync(settings, RunNotificationKind.Result, "结果", "内容");
        Assert.False(result.Sent, "错误不能报成功");
        Assert.False(result.Error!.Contains(secret), "不输出响应原文");
        if (content.StartsWith("broken", StringComparison.Ordinal))
            Assert.Equal("Bark 响应不是有效的 JSON", result.Error, "解析失败独立分类");
    }
    using var failureClient = new HttpClient(new NotificationTestHandler((_, _) => throw new HttpRequestException(secret)));
    var failed = await new BarkNotificationService(failureClient).SendAsync(settings, RunNotificationKind.Result, "结果", "内容");
    Assert.False(failed.Error!.Contains(secret), "异常不泄漏密钥");
    Assert.True(failed.Error.Contains("HTTP 请求失败"), "请求异常需要与响应解析失败区分");
    using var dnsClient = new HttpClient(new NotificationTestHandler((_, _) =>
        throw new HttpRequestException(HttpRequestError.NameResolutionError, secret)));
    var dnsFailure = await new BarkNotificationService(dnsClient).SendAsync(settings, RunNotificationKind.Reminder, "提醒", "内容");
    Assert.True(dnsFailure.Error!.Contains("NameResolutionError"), "保留脱敏的网络错误分类");
    using var rejectedClient = new HttpClient(new NotificationTestHandler((_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(secret) })));
    var rejected = await new BarkNotificationService(rejectedClient).SendAsync(settings, RunNotificationKind.Result, "结果", "内容");
    Assert.Equal("Bark 服务返回 HTTP 403", rejected.Error, "只记录 HTTP 状态");
    foreach (var address in new[] { "", "file:///secret", "https://bark.invalid/", "https://user:pass@bark.invalid/key", "https://bark.invalid/key?token=secret" })
    {
        settings.BarkAddress = address;
        var invalid = await new BarkNotificationService(failureClient).SendAsync(settings, RunNotificationKind.Result, "结果", "内容");
        Assert.True(invalid.Error!.StartsWith("Bark 地址无效"), "非法地址不发请求");
    }
}

static async Task BarkTimeoutAndCancellationAsync()
{
    var settings = new AppSettings { NotificationsEnabled = true, BarkAddress = "https://bark.invalid/device" };
    using var client = new HttpClient(new NotificationTestHandler((_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new NotificationSlowContent() })));
    var watch = Stopwatch.StartNew();
    var result = await new BarkNotificationService(client).SendAsync(settings, RunNotificationKind.Result, "结果", "内容");
    Assert.Equal("通知发送超时", result.Error, "正文也受五秒超时约束");
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "超时有界");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var cancelled = await new BarkNotificationService(client).SendAsync(settings, RunNotificationKind.Result, "结果", "内容", cancellation.Token);
    Assert.Equal("通知发送已取消", cancelled.Error, "关闭时取消发送");
}

static async Task NotificationJournalAsync()
{
    using var area = TestArea.Create();
    const string secret = "private-device-key";
    var settings = new AppSettings { NotificationsEnabled = true, BarkAddress = "https://bark.invalid/" + secret };
    using var client = new HttpClient(new NotificationTestHandler((_, _) =>
        throw new HttpRequestException(HttpRequestError.NameResolutionError, secret)));
    var delivery = await new BarkNotificationService(client).SendAsync(settings, RunNotificationKind.Reminder, secret, secret);
    BarkNotificationService.RecordDelivery(RunNotificationKind.Reminder, delivery, area.Root, "08:00");
    var journal = await File.ReadAllTextAsync(area.File("notifications.jsonl"));
    using var entry = JsonDocument.Parse(journal);
    Assert.Equal("发送失败", entry.RootElement.GetProperty("Outcome").GetString(), "错误不会伪装成接收成功");
    Assert.Equal("Reminder", entry.RootElement.GetProperty("Kind").GetString(), "提醒和结果可以区分");
    Assert.True(entry.RootElement.GetProperty("Reason").GetString()!.Contains("NameResolutionError"), "安全错误分类保留");
    Assert.False(journal.Contains(secret) || journal.Contains("bark.invalid"), "日志不记录地址密钥或正文");
    Assert.False((await File.ReadAllTextAsync(area.File("crashes/crash.jsonl"))).Contains(secret), "诊断日志同样脱敏");
    var blockedRoot = area.File("blocked");
    await File.WriteAllTextAsync(blockedRoot, "occupied");
    BarkNotificationService.RecordDelivery(RunNotificationKind.Result, new(true), blockedRoot);
}

static Task WorkflowSummaryIncludesFailuresAsync()
{
    var id = Guid.NewGuid();
    var start = DateTimeOffset.Now;
    var record = new RunRecord { ToolId = ToolId.Maa, ToolName = "MAA", StartedAt = start,
        EndedAt = start.AddMinutes(2), State = RunState.CompletedWithErrors, Message = "任务步骤异常",
        WorkflowRunId = id, Channel = 2, LogExcerpt = ["isolated error evidence"] };
    var summary = WorkflowRunSummary.Create(id, start, start.AddMinutes(3),
        [new() { ToolId = ToolId.Maa, IsEnabled = true, Channel = 2 }, new() { ToolId = ToolId.BetterGi, IsEnabled = true }],
        [record, record with { WorkflowRunId = Guid.NewGuid(), Message = "other-workflow" }],
        QueueRunResult.NotAllPlannedTasksCompleted, false, "启动被阻止");
    Assert.True(summary.Title.Contains("需要检查"), "异常标题");
    Assert.True(summary.Body.Contains("执行异常") && summary.Body.Contains("未运行"), "异常和缺失任务");
    Assert.True(summary.Body.Contains("3分0秒") && summary.Body.Contains("2分0秒"), "整轮墙钟和各工具耗时");
    Assert.True(summary.Body.Contains("历史保存失败"), "持久化失败可见");
    Assert.False(summary.Body.Contains("轮次") || summary.Body.Contains("在 GachaOps 查看"), "通知不显示轮次码或详情引导");
    Assert.True(summary.Details.Contains(id.ToString()) && summary.Details.Contains("isolated error evidence"), "详情保留身份和日志");
    Assert.False(summary.Details.Contains("other-workflow"), "隔离其他轮次");
    return Task.CompletedTask;
}

static Task WorkflowSummarySuccessPolicyAsync()
{
    var id = Guid.NewGuid();
    var now = DateTimeOffset.Now;
    var tasks = new[] { new WorkflowTaskSetting { ToolId = ToolId.Maa, IsEnabled = true } };
    var record = new RunRecord { ToolId = ToolId.Maa, ToolName = "MAA", StartedAt = now, EndedAt = now,
        State = RunState.Succeeded, Message = "完成", WorkflowRunId = id };
    var summary = WorkflowRunSummary.Create(id, now, now, tasks, [record], QueueRunResult.AllPlannedTasksCompleted, true);
    Assert.True(summary.Title.Contains("工具任务已完成"), "仅说明工具任务完成");
    foreach (var state in new[] { RunState.Failed, RunState.TimedOut, RunState.CompletedWithErrors, RunState.Skipped, RunState.Cancelled })
        Assert.True(WorkflowRunSummary.Create(id, now, now, tasks, [record with { State = state }],
            QueueRunResult.AllPlannedTasksCompleted, true).Title.Contains("需要检查"), "不因队列结果覆盖工具异常");
    Assert.True(WorkflowRunSummary.Create(id, now, now, tasks, [], QueueRunResult.AllPlannedTasksCompleted, true).Title.Contains("需要检查"), "缺失记录不报成功");
    Assert.True(WorkflowRunSummary.Create(id, now, now, tasks, [record], QueueRunResult.AllPlannedTasksCompleted, false).Title.Contains("需要检查"), "历史失败不报成功");
    Assert.True(WorkflowRunSummary.Create(id, now, now, tasks,
        [record with { State = RunState.Failed, Message = " earlier failure " }, record],
        QueueRunResult.AllPlannedTasksCompleted, true).Body.Contains("earlier failure"), "同工具后续成功不能隐藏已有异常");
    return Task.CompletedTask;
}

static async Task UnsafeInstallationBlocksOtherUpdatesAsync()
{
    using var area = TestArea.Create();
    var unsafeTool = new FakeToolUpdateProvider(ToolId.BetterGi)
    {
        CheckHandler = _ => throw new HttpRequestException("检查失败"),
        InstallationHandler = _ => throw new InvalidDataException("安装损坏")
    };
    var other = new FakeToolUpdateProvider(ToolId.Maa, updateAvailable: true);
    var coordinator = new ToolUpdateCoordinator([unsafeTool, other], new ToolUpdateStateStore(area.Root));
    IAutomationAdapter[] adapters = [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)];
    var workflow = Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2));
    var settings = new AppSettings { UpdateToolsBeforeLaunch = true };
    var result = await coordinator.PrepareAsync(adapters, workflow, settings);
    Assert.False(result.Succeeded, "不安全安装阻止整轮");
    Assert.Equal(0, result.RunnableTasks.Count, "所有任务禁止启动");
    Assert.Equal(1, other.CheckCount, "并行检查已完成");
    Assert.Equal(0, other.UpdateCount, "不得继续开始更新");
    Assert.True(result.BlockReason!.Contains("请检查配置或安装"), "提示检查配置或安装");
    var repeated = await coordinator.PrepareAsync(adapters, workflow, settings);
    Assert.False(repeated.Succeeded, "重新开始仍须检查本地安装");
    Assert.Equal(1, unsafeTool.CheckCount, "联网失败也不重复检查");
    Assert.Equal(1, other.CheckCount, "其他工具也不重复检查");
}

static async Task PreparationFailureBlocksWholeWorkflowAsync()
{
    foreach (var providerFailure in new[] { false, true })
    {
        using var area = TestArea.Create();
        var invalid = new PreflightAdapter(ToolId.BetterGi)
        {
            ValidationHandler = _ => providerFailure ? ValidationResult.Success()
                : new ValidationResult(false, ["配置不存在"])
        };
        var first = new FakeToolUpdateProvider(ToolId.BetterGi)
        {
            Validation = providerFailure ? new ValidationResult(false, ["更新入口缺失"])
                : ValidationResult.Success()
        };
        var other = new FakeToolUpdateProvider(ToolId.Maa, updateAvailable: true);
        var result = await new ToolUpdateCoordinator([first, other], new ToolUpdateStateStore(area.Root))
            .PrepareAsync([invalid, new PreflightAdapter(ToolId.Maa)],
                Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)),
                new AppSettings { UpdateToolsBeforeLaunch = true });
        Assert.False(result.Succeeded, "预检失败阻止整轮");
        Assert.Equal(0, result.RunnableTasks.Count, "不能放行安全子集");
        Assert.Equal(0, other.CheckCount, "失败后不再开始联网检查");
        Assert.Equal(0, other.UpdateCount, "失败后不再开始更新");
        Assert.Equal(2, result.HistoryRecords.Count, "失败与跳过各记录一次");
        Assert.Equal(RunState.Failed, result.HistoryRecords.Single(r => r.ToolId == ToolId.BetterGi).State, "保留失败");
        Assert.Equal(RunState.Skipped, result.HistoryRecords.Single(r => r.ToolId == ToolId.Maa).State, "其他任务跳过");
        Assert.True(result.HistoryRecords.All(r => r.WorkflowRunId == result.WorkflowRunId), "保留整轮身份");
    }
}

static async Task UnselectedRunningToolBlocksPreparationAsync()
{
    foreach (var id in Enum.GetValues<ToolId>())
    {
        using var area = TestArea.Create();
        var selectedId = id == ToolId.Maa ? ToolId.BetterGi : ToolId.Maa;
        var busy = new FakeAdapter(id) { ProcessRunning = true };
        var selected = new FakeAdapter(selectedId, RunState.Succeeded);
        var provider = new FakeToolUpdateProvider(selectedId, updateAvailable: true);
        var result = await new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root))
            .PrepareAsync([busy, selected], Workflow((selectedId, 1)),
                new AppSettings { UpdateToolsBeforeLaunch = true });
        Assert.False(result.Succeeded, "未勾选工具也必须阻止准备");
        Assert.Equal(0, result.RunnableTasks.Count, "本轮没有可运行任务");
        Assert.Equal(0, provider.CheckCount, "入口拦截不消耗更新检查机会");
        Assert.Equal(0, selected.StartCount, "不能启动已选工具");
    }
}

static async Task RunningExcludedToolBlocksPreparedWorkflowAsync()
{
    using var current = Process.GetCurrentProcess();
    foreach (var updateEnabled in new[] { false, true })
    {
        using var area = TestArea.Create();
        var busy = new BetterGiAdapter();
        var next = new FakeAdapter(ToolId.MaaEnd, RunState.Succeeded);
        var other = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
        var workflow = Workflow((ToolId.BetterGi, 1), (ToolId.MaaEnd, 1), (ToolId.Maa, 2));
        var settings = new AppSettings
        {
            BetterGiPath = current.MainModule!.FileName,
            UpdateToolsBeforeLaunch = updateEnabled,
            ExitAfterWorkflowCompletes = true
        };
        var preparation = await new ToolUpdateCoordinator([], new ToolUpdateStateStore(area.Root))
            .PrepareAsync([busy, next, other], workflow, settings);
        Assert.Equal(0, preparation.RunnableTasks.Count, "入口发现进程后整轮停止");
        Assert.Equal("BetterGI 仍在运行，请退出后重试", preparation.BlockReason, "给出可操作的提示");
        Assert.Equal(3, preparation.HistoryRecords.Count, "失败和两个跳过任务各记录一次");
        Assert.Equal(RunState.Failed, preparation.HistoryRecords.Single(r => r.ToolId == ToolId.BetterGi).State, "保留失败");
        Assert.True(preparation.HistoryRecords.Where(r => r.ToolId != ToolId.BetterGi).All(r => r.State == RunState.Skipped), "其余任务跳过");
        Assert.True(preparation.HistoryRecords.All(r => r.WorkflowRunId == preparation.WorkflowRunId), "保留整轮身份");
        Assert.Equal(3, preparation.HistoryRecords.Select(r => r.TaskExecutionId).Distinct().Count(), "任务身份独立");
        Assert.False(current.HasExited, "进程检查不结束已有进程");
    }
}

static async Task RunningToolAfterPreparationBlocksAndResetsAsync()
{
    using var area = TestArea.Create();
    var first = new FakeAdapter(ToolId.BetterGi, RunState.Succeeded);
    var other = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
    IAutomationAdapter[] adapters = [first, other];
    var workflow = Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2));
    var settings = new AppSettings();
    var preparation = await new ToolUpdateCoordinator([], new ToolUpdateStateStore(area.Root))
        .PrepareAsync(adapters, workflow, settings);
    Assert.Equal(2, preparation.RunnableTasks.Count, "准备时两个工具均可运行");
    first.ProcessRunning = true;
    var queue = new AutomationQueueService();
    var records = new ConcurrentBag<RunRecord>();
    queue.RunRecorded += records.Add;
    var blocked = await queue.RunAsync(adapters, preparation.RunnableTasks, settings,
        preparedWorkflowRunId: preparation.WorkflowRunId);
    Assert.Equal(0, first.StartCount, "启动前才出现的进程也能拦截");
    Assert.Equal(0, other.StartCount, "另一通道不能抢先启动");
    Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, blocked, "拦截结果未完成");
    Assert.Equal(2, records.Count, "两个任务各记录一次");
    Assert.True(records.All(record => record.State == RunState.Skipped), "未尝试启动的任务全部跳过");
    Assert.True(queue.StartupBlockReason is not null, "保留启动拦截原因");
    first.ProcessRunning = false;
    var completed = await queue.RunAsync(adapters, workflow, settings);
    Assert.Equal(QueueRunResult.AllPlannedTasksCompleted, completed, "退出工具后新一轮能运行");
    Assert.Equal(1, first.StartCount, "新一轮只启动一次");
    Assert.Equal(1, other.StartCount, "新一轮两个通道正常运行");
    Assert.True(queue.StartupBlockReason is null, "不得沿用上一轮拦截原因");
}

static async Task UnplannedRunningToolBlocksAsync()
{
    foreach (var includeDisabled in new[] { false, true })
    {
        var unplanned = new FakeAdapter(ToolId.BetterGi) { ProcessRunning = true };
        var selected = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
        var workflow = Workflow((ToolId.Maa, 1)).ToList();
        if (includeDisabled)
            workflow.Add(new WorkflowTaskSetting { ToolId = ToolId.BetterGi, IsEnabled = false, Channel = 2 });
        var queue = new AutomationQueueService();
        var result = await queue.RunAsync([unplanned, selected], workflow, new AppSettings());
        Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, result, "未勾选工具也阻止启动");
        Assert.Equal(0, selected.StartCount, "已选工具不能启动");
        Assert.Equal(0, unplanned.StartCount, "未参与的工具不启动");
        Assert.Equal("BetterGI 仍在运行，请退出后重试", queue.StartupBlockReason, "说明阻止原因");
    }
}

static async Task ExitedExcludedToolDoesNotBlockAsync()
{
    using var area = TestArea.Create();
    var busy = new FakeAdapter(ToolId.BetterGi) { ProcessRunning = true };
    var selected = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
    IAutomationAdapter[] adapters = [busy, selected];
    var workflow = Workflow((ToolId.Maa, 2));
    var settings = new AppSettings { UpdateToolsBeforeLaunch = true };
    var provider = new FakeToolUpdateProvider(ToolId.Maa);
    var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root));
    var blocked = await coordinator.PrepareAsync(adapters, workflow, settings);
    Assert.Equal(0, blocked.RunnableTasks.Count, "存在进程时整轮停止");
    Assert.Equal(0, provider.CheckCount, "拦截不消耗检查机会");
    busy.ProcessRunning = false;
    var preparation = await coordinator.PrepareAsync(adapters, workflow, settings);
    Assert.True(preparation.Succeeded, "退出后新一轮正常准备");
    Assert.Equal(1, provider.CheckCount, "本会话首次检查");
    await coordinator.PrepareAsync(adapters, workflow, settings);
    Assert.Equal(1, provider.CheckCount, "再次开始不重复联网");
    var queue = new AutomationQueueService();
    var result = await queue.RunAsync(adapters, preparation.RunnableTasks, settings,
        preparedWorkflowRunId: preparation.WorkflowRunId);
    Assert.Equal(1, selected.StartCount, "退出后重新开始可以运行");
    Assert.Equal(0, busy.StartCount, "未勾选工具不启动");
    Assert.Equal(QueueRunResult.AllPlannedTasksCompleted, result, "本轮正常完成");
}

static async Task SelectedToolsPersistAsync()
{
    using var area = TestArea.Create();
    var store = new SettingsStore(area.Root);
    var settings = new AppSettings
    {
        MaaProfile = "保留配置",
        WorkflowTasks =
        [
            new() { ToolId = ToolId.MaaEnd, Channel = 2, IsEnabled = false },
            new() { ToolId = ToolId.BetterGi, Channel = 1, IsEnabled = true }
        ]
    };
    var expected = settings.WorkflowTasks.ToArray();
    await store.SaveAsync(settings);
    var loaded = (await store.LoadAsync()).Settings;
    Assert.SequenceEqual(expected, loaded.WorkflowTasks!, "不能补回未选择的工具或改变顺序和启停状态");
    loaded.WorkflowTasks = [];
    await store.SaveAsync(loaded);
    loaded = (await store.LoadAsync()).Settings;
    Assert.Equal(0, loaded.WorkflowTasks!.Count, "全部取消后重启不能补回工具");
    Assert.Equal("保留配置", loaded.MaaProfile, "移除工具不能清空配置");
}

static async Task NewSettingsStartWithoutToolsAsync()
{
    using var area = TestArea.Create();
    var settings = (await new SettingsStore(area.Root).LoadAsync()).Settings;
    Assert.Equal(0, settings.WorkflowTasks!.Count, "首次使用应由用户选择工具");
}

static Task UpdatePreflightRejectsRunningEntryAsync()
{
    using var process = Process.GetCurrentProcess();
    var provider = new VersionTestUpdateProvider("1.0.0", "2.0.0", process.MainModule!.FileName!);
    var validation = provider.ValidateUpdate(new AppSettings());
    Assert.False(validation.IsValid, "当前进程作为官方入口时必须拒绝重复更新");
    Assert.True(validation.Issues.Any(issue => issue.Contains("已经在运行", StringComparison.Ordinal)),
        "应明确报告官方入口正在运行");
    return Task.CompletedTask;
}

static async Task ReleaseCheckTimeoutIncludesBodyAsync()
{
    using var client = new HttpClient(new DelayedReleaseBodyHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var timedOut = false;
    try
    {
        await new GitHubReleaseClient(client).GetLatestVersionAsync("test/releases", cancellation.Token);
    }
    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
    {
        timedOut = true;
    }
    Assert.True(timedOut, "收到响应头后，停滞正文也必须受 HttpClient 超时限制");
}

static async Task UnsafeFailureRecordsSkippedOnceAsync()
{
    foreach (var state in new[] { RunState.Failed, RunState.TimedOut })
    {
        var queue = new AutomationQueueService();
        var records = new ConcurrentBag<RunRecord>();
        var statuses = new ConcurrentBag<ToolStatusUpdate>();
        queue.RunRecorded += records.Add;
        queue.StatusChanged += statuses.Add;
        var first = new FakeAdapter(ToolId.BetterGi, state, RunState.Succeeded);
        var next = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
        var gate = NewGate();
        var other = new FakeAdapter(ToolId.MaaEnd, RunState.Succeeded) { MonitorGate = gate.Task };
        var skipped = NewGate();
        queue.StatusChanged += update => { if (update.State == RunState.Skipped) skipped.TrySetResult(); };
        var run = queue.RunAsync([first, next, other],
            Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 1), (ToolId.MaaEnd, 2)), new AppSettings());
        await Task.WhenAll(skipped.Task, other.Started.Task).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(run.IsCompleted, "本通道结束仍须等待其他通道");
        gate.SetResult();
        Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, await run.WaitAsync(TimeSpan.FromSeconds(2)), "不人工阻塞");
        Assert.Equal(1, first.StartCount, "失败或超时不重试");
        Assert.Equal(0, next.StartCount, "未知安全性时禁止下一项");
        Assert.Equal(1, other.StartCount, "其他通道不受影响");
        Assert.Equal(3, records.Count, "本项失败、后继跳过、其他通道各一条历史");
        Assert.Equal(state, records.Single(record => record.ToolId == first.Id).State, "失败终态不能被跳过覆盖");
        var skippedRecord = records.Single(record => record.ToolId == next.Id);
        var queued = statuses.Single(update => update.ToolId == next.Id && update.State == RunState.Queued);
        Assert.Equal(RunState.Skipped, skippedRecord.State, "未启动项保存跳过");
        Assert.Equal(queued.TaskExecutionId, skippedRecord.TaskExecutionId, "保留原任务身份");
        Assert.True(records.All(record => record.WorkflowRunId == queued.WorkflowRunId), "同轮身份一致");
    }
}

static async Task UnstartedProductionFailureContinuesAsync()
{
    using var area = TestArea.Create();
    foreach (var adapter in new ProcessAutomationAdapter[] { new BetterGiAdapter(), new MaaAdapter(), new MaaEndAdapter() })
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var queue = new AutomationQueueService();
        var nextId = adapter.Id == ToolId.Maa ? ToolId.BetterGi : ToolId.Maa;
        var next = new FakeAdapter(nextId, RunState.Succeeded);
        var records = new List<RunRecord>();
        queue.RunRecorded += records.Add;
        var missing = area.File($"missing-{Guid.NewGuid():N}.exe");
        var settings = new AppSettings { BetterGiPath = missing, MaaPath = missing, MaaEndPath = missing };
        var result = await queue.RunAsync([adapter, next],
            Workflow((adapter.Id, 1), (next.Id, 1)), settings, cancellation.Token);
        Assert.Equal(1, next.StartCount, "确定未启动的失败不得等待人工，应自动继续");
        Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, result, "不能掩盖失败");
        Assert.Equal(RunState.Failed, records[0].State, "本项保留失败");
        Assert.Equal(2, records.Count, "失败与后继各一次历史");
    }
}

static async Task ExistingProductionToolBlocksContinuationAsync()
{
    using var current = Process.GetCurrentProcess();
    foreach (var adapter in new ProcessAutomationAdapter[] { new BetterGiAdapter(), new MaaAdapter(), new MaaEndAdapter() })
    {
        var path = current.MainModule!.FileName;
        var settings = new AppSettings { BetterGiPath = path, MaaPath = path, MaaEndPath = path };
        Assert.False(adapter.CanContinueAfterUnstartedFailure(settings), "只读检查发现工具进程时不得继续");
        Assert.False(adapter.CanContinueAfterUnstartedFailure(new AppSettings()), "缺少路径无法检查时保守未知");
        var next = new FakeAdapter(adapter.Id == ToolId.Maa ? ToolId.BetterGi : ToolId.Maa, RunState.Succeeded);
        var queue = new AutomationQueueService();
        var records = new List<RunRecord>();
        queue.RunRecorded += records.Add;
        await queue.RunAsync([adapter, next], Workflow((adapter.Id, 1), (next.Id, 1)), settings)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, next.StartCount, "存在进程的预检失败必须结束本通道");
        Assert.Equal(RunState.Skipped, records.Single(record => record.ToolId == next.Id).State, "跳过保留历史");
        Assert.False(current.HasExited, "只读检查不得结束进程");
    }
}

static async Task FailureEventCleanupAllowsNextWorkflowAsync()
{
    var queue = new AutomationQueueService();
    var throwOnce = true;
    queue.RunRecorded += _ =>
    {
        if (throwOnce)
        {
            throwOnce = false;
            throw new InvalidOperationException("测试订阅异常");
        }
    };
    try
    {
        await queue.RunAsync([new FakeAdapter(ToolId.BetterGi, RunState.Failed)],
            Workflow((ToolId.BetterGi, 1)), new AppSettings());
        throw new Exception("应保留订阅异常");
    }
    catch (InvalidOperationException exception) when (exception.Message == "测试订阅异常") { }
    Assert.False(queue.IsRunning, "异常后释放工作流状态");
    var records = new List<RunRecord>();
    queue.RunRecorded += records.Add;
    for (var i = 0; i < 2; i++)
        await queue.RunAsync([new FakeAdapter(ToolId.BetterGi, RunState.Failed)],
            Workflow((ToolId.BetterGi, 1)), new AppSettings()).WaitAsync(TimeSpan.FromSeconds(2));
    Assert.Equal(2, records.Select(record => record.WorkflowRunId).Distinct().Count(), "新轮不继承旧工作流身份");
    Assert.Equal(2, records.Select(record => record.TaskExecutionId).Distinct().Count(), "新轮不继承旧任务身份");
}

static async Task ToolPathsDefaultToEmptyAsync()
{
    var newSettings = new AppSettings();
    Assert.Equal(string.Empty, newSettings.BetterGiPath, "新设置的 BetterGI 程序路径应为空");
    Assert.Equal(string.Empty, newSettings.MaaPath, "新设置的 MAA 程序路径应为空");
    Assert.Equal(string.Empty, newSettings.MaaEndPath, "新设置的 MaaEnd 程序路径应为空");

    using var area = TestArea.Create();
    var loadedSettings = (await new SettingsStore(area.Root).LoadAsync()).Settings;
    Assert.Equal(string.Empty, loadedSettings.BetterGiPath, "缺失 settings.json 时 BetterGI 程序路径应为空");
    Assert.Equal(string.Empty, loadedSettings.MaaPath, "缺失 settings.json 时 MAA 程序路径应为空");
    Assert.Equal(string.Empty, loadedSettings.MaaEndPath, "缺失 settings.json 时 MaaEnd 程序路径应为空");

    await File.WriteAllTextAsync(area.File("settings.json"), "{");
    var fallbackSettings = (await new SettingsStore(area.Root).LoadAsync()).Settings;
    Assert.Equal(string.Empty, fallbackSettings.BetterGiPath, "settings.json 损坏回退时 BetterGI 程序路径应为空");
    Assert.Equal(string.Empty, fallbackSettings.MaaPath, "settings.json 损坏回退时 MAA 程序路径应为空");
    Assert.Equal(string.Empty, fallbackSettings.MaaEndPath, "settings.json 损坏回退时 MaaEnd 程序路径应为空");
}

static async Task CorruptSettingsIsPreservedAndReportedOnceAsync()
{
    using var area = TestArea.Create();
    var settingsPath = area.File("settings.json");
    var corruptPath = area.File("settings.corrupt.json");
    var corruptBytes = Encoding.UTF8.GetBytes("{ 不是有效的 JSON");
    await File.WriteAllBytesAsync(settingsPath, corruptBytes);

    var result = await new SettingsStore(area.Root).LoadAsync();
    var settings = result.Settings;
    var recoveredFromCorruptSettings = result.RecoveredFromCorruptSettings;

    Assert.Equal(string.Empty, settings.BetterGiPath, "损坏设置恢复后应使用规范化默认设置");
    Assert.True(recoveredFromCorruptSettings, "首次从损坏设置恢复时应返回恢复标记");
    Assert.False(File.Exists(settingsPath), "损坏 settings.json 不应继续作为活动设置存在");
    Assert.True(File.Exists(corruptPath), "损坏 settings.json 应保留为固定备份");
    Assert.SequenceEqual(corruptBytes, await File.ReadAllBytesAsync(corruptPath),
        "损坏设置备份应与原文件字节完全一致");
}

static async Task CorruptSettingsRecoveryIsNotReportedTwiceAsync()
{
    using var area = TestArea.Create();
    var settingsPath = area.File("settings.json");
    await File.WriteAllTextAsync(settingsPath, "{");
    var store = new SettingsStore(area.Root);

    var first = await store.LoadAsync();
    var second = await store.LoadAsync();
    var secondSettings = second.Settings;
    var firstRecovered = first.RecoveredFromCorruptSettings;
    var secondRecovered = second.RecoveredFromCorruptSettings;

    Assert.True(firstRecovered, "首次加载损坏设置应返回恢复标记");
    Assert.Equal(string.Empty, secondSettings.BetterGiPath, "再次加载应继续返回规范化默认设置");
    Assert.False(secondRecovered, "损坏文件已保留后再次加载不应重复返回恢复标记");
}

static async Task StructurallyInvalidSettingsAreRecoveredOnceAsync()
{
    (string Name, string Json)[] cases =
    [
        ("顶层 null", "null"),
        ("顶层数组", "[]"),
        ("BetterGiPath 为 null", """{"BetterGiPath":null}"""),
        ("BetterGiMode 为 null", """{"BetterGiMode":null}"""),
        ("BetterGiProfile 为 null", """{"BetterGiProfile":null}"""),
        ("MaaPath 为 null", """{"MaaPath":null}"""),
        ("MaaProfile 为 null", """{"MaaProfile":null}"""),
        ("MaaEndPath 为 null", """{"MaaEndPath":null}"""),
        ("MaaEndInstance 为 null", """{"MaaEndInstance":null}""")
    ];

    foreach (var testCase in cases)
    {
        using var area = TestArea.Create();
        var settingsPath = area.File("settings.json");
        var corruptPath = area.File("settings.corrupt.json");
        var originalBytes = Encoding.UTF8.GetBytes(testCase.Json);
        await File.WriteAllBytesAsync(settingsPath, originalBytes);
        var store = new SettingsStore(area.Root);

        var first = await store.LoadAsync();
        var second = await store.LoadAsync();

        Assert.True(first.RecoveredFromCorruptSettings,
            $"{testCase.Name}首次加载应返回恢复标记");
        Assert.Equal(string.Empty, first.Settings.BetterGiPath,
            $"{testCase.Name}恢复后应返回规范化默认设置");
        Assert.False(File.Exists(settingsPath),
            $"{testCase.Name}不应继续作为活动设置");
        Assert.SequenceEqual(originalBytes, await File.ReadAllBytesAsync(corruptPath),
            $"{testCase.Name}的固定备份应保留原始字节");
        Assert.False(second.RecoveredFromCorruptSettings,
            $"{testCase.Name}恢复后第二次加载不应重复报告");
    }
}

static async Task CompatibleSettingsShapesContinueToLoadAsync()
{
    (string Name, string Json, Action<AppSettings> AssertSettings)[] cases =
    [
        ("空对象", "{}", settings =>
            Assert.Equal("Default", settings.MaaProfile, "空对象应保留缺失字段的默认值")),
        ("缺失字段", """{"BetterGiProfile":"兼容配置"}""", settings =>
            Assert.Equal("兼容配置", settings.BetterGiProfile, "已存在字段应正常加载")),
        ("未知字段", """{"FutureOption":{"Enabled":true}}""", settings =>
            Assert.Equal("OneDragon", settings.BetterGiMode, "未知字段不应影响已知设置")),
        ("旧 QueueOrder", """{"QueueOrder":["MaaEnd","BetterGi"]}""", settings =>
            Assert.SequenceEqual(
                [ToolId.MaaEnd, ToolId.BetterGi, ToolId.Maa],
                settings.WorkflowTasks!.Select(task => task.ToolId),
                "旧 QueueOrder 应继续迁移")),
        ("WorkflowTasks 为 null", """{"WorkflowTasks":null}""", settings =>
            Assert.SequenceEqual(
                [ToolId.BetterGi, ToolId.Maa, ToolId.MaaEnd],
                settings.WorkflowTasks!.Select(task => task.ToolId),
                "WorkflowTasks 为 null 应作为兼容输入规范化"))
    ];

    foreach (var testCase in cases)
    {
        using var area = TestArea.Create();
        var settingsPath = area.File("settings.json");
        var corruptPath = area.File("settings.corrupt.json");
        await File.WriteAllTextAsync(settingsPath, testCase.Json);

        var result = await new SettingsStore(area.Root).LoadAsync();

        Assert.False(result.RecoveredFromCorruptSettings,
            $"{testCase.Name}不应被误判为损坏设置");
        Assert.True(File.Exists(settingsPath),
            $"{testCase.Name}应继续作为活动设置");
        Assert.False(File.Exists(corruptPath),
            $"{testCase.Name}不应创建损坏备份");
        testCase.AssertSettings(result.Settings);
    }
}

static async Task LockedSettingsFileThrowsWithoutFallbackAsync()
{
    using var area = TestArea.Create();
    var settingsPath = area.File("settings.json");
    var corruptPath = area.File("settings.corrupt.json");
    var originalBytes = Encoding.UTF8.GetBytes("""
        {
          "BetterGiProfile": "有效配置"
        }
        """);
    await File.WriteAllBytesAsync(settingsPath, originalBytes);

    await using (var lockStream = new FileStream(
                     settingsPath,
                     FileMode.Open,
                     FileAccess.ReadWrite,
                     FileShare.None))
    {
        IOException? observed = null;
        try
        {
            _ = await new SettingsStore(area.Root).LoadAsync();
        }
        catch (IOException exception)
        {
            observed = exception;
        }

        Assert.True(observed is not null, "临时读取失败必须抛出 IOException，不能返回默认设置");
    }

    Assert.SequenceEqual(originalBytes, await File.ReadAllBytesAsync(settingsPath),
        "读取失败不得改变原 settings.json 字节");
    Assert.False(File.Exists(corruptPath), "读取失败不得创建损坏设置备份");
}

static async Task ToolPathsRoundTripAsync()
{
    using var area = TestArea.Create();
    var store = new SettingsStore(area.Root);
    var expected = new AppSettings
    {
        BetterGiPath = @"C:\Tools\BetterGI\BetterGI.exe",
        MaaPath = @"D:\Games With Spaces\MAA\MAA.exe",
        MaaEndPath = @"C:\Tools\MaaEnd\MaaEnd.exe"
    };

    await store.SaveAsync(expected);
    var restored = (await store.LoadAsync()).Settings;

    Assert.Equal(expected.BetterGiPath, restored.BetterGiPath, "已保存的 BetterGI 程序路径未原样恢复");
    Assert.Equal(expected.MaaPath, restored.MaaPath, "已保存的 MAA 程序路径未原样恢复");
    Assert.Equal(expected.MaaEndPath, restored.MaaEndPath, "已保存的 MaaEnd 程序路径未原样恢复");
}

static Task StartupAutomationSettingsDefaultToDisabledAsync()
{
    var settings = new AppSettings();
    Assert.False(settings.MinimizeOnStartup, "启动后最小化应默认关闭");
    Assert.False(settings.RunWorkflowOnStartup, "启动后自动运行应默认关闭");
    Assert.False(settings.ExitAfterWorkflowCompletes, "任务结束后退出应默认关闭");
    Assert.False(settings.UpdateToolsBeforeLaunch, "启动前自动更新应默认关闭");
    return Task.CompletedTask;
}

static async Task LegacySettingsWithoutStartupAutomationLoadAsync()
{
    var area = TestArea.Create();
    try
    {
        await File.WriteAllTextAsync(area.File("settings.json"), """
            {
              "BetterGiProfile": "旧配置",
              "NoLogTimeoutMinutes": 12,
              "HardTimeoutMinutes": 90
            }
            """);

        var settings = (await new SettingsStore(area.Root).LoadAsync()).Settings;
        Assert.Equal("旧配置", settings.BetterGiProfile, "旧设置内容未正常加载");
        Assert.False(settings.MinimizeOnStartup, "旧设置不应自动开启启动后最小化");
        Assert.False(settings.RunWorkflowOnStartup, "旧设置不应自动开启启动后运行");
        Assert.False(settings.ExitAfterWorkflowCompletes, "旧设置不应自动开启任务结束后退出");
        Assert.False(settings.UpdateToolsBeforeLaunch, "旧设置缺少字段时应默认关闭启动前自动更新");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task StartupAutomationSettingsRoundTripAsync()
{
    var area = TestArea.Create();
    try
    {
        var store = new SettingsStore(area.Root);
        await store.SaveAsync(new AppSettings
        {
            MinimizeOnStartup = true,
            RunWorkflowOnStartup = true,
            ExitAfterWorkflowCompletes = true,
            UpdateToolsBeforeLaunch = true
        });

        var restored = (await store.LoadAsync()).Settings;
        Assert.True(restored.MinimizeOnStartup, "启动后最小化未持久化");
        Assert.True(restored.RunWorkflowOnStartup, "启动后自动运行未持久化");
        Assert.True(restored.ExitAfterWorkflowCompletes, "任务结束后退出未持久化");
        Assert.True(restored.UpdateToolsBeforeLaunch, "显式开启的启动前自动更新开关未持久化");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task DisabledToolUpdateSkipsProvidersAsync()
{
    using var area = TestArea.Create();
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi)
    {
        CheckHandler = _ => throw new InvalidOperationException("关闭开关后不应检查版本")
    };
    var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root));

    var result = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings { UpdateToolsBeforeLaunch = false });

    Assert.True(result.Succeeded, "关闭自动更新后，正常的本地预检应继续通过");
    Assert.Equal(0, provider.CheckCount, "关闭自动更新后不应调用 provider 进行联网检查");
}

static async Task ToolUpdateProviderComparesPrereleaseVersionsAsync()
{
    var cases = new (string Current, string Latest, bool UpdateAvailable)[]
    {
        ("v2.26.0-beta.6", "v2.26.0", true),
        ("2.27.0-beta.1", "2.26.0", false),
        ("2.26.0-alpha.2", "2.26.0-beta.1", true),
        ("2.26.0-beta.9", "2.26.0-beta.10", true),
        ("2.26.0-rc.1", "2.26.0", true),
        ("1.2.3.4", "1.2.3.5", true),
        ("1.2.3+local.7", "1.2.3+official.9", false)
    };

    foreach (var testCase in cases)
    {
        using var area = TestArea.Create();
        var executablePath = area.File("version-test.exe");
        await File.WriteAllBytesAsync(executablePath, [0x4D, 0x5A, 0x00, 0x00]);
        var provider = new VersionTestUpdateProvider(
            testCase.Current,
            testCase.Latest,
            executablePath);

        var result = await provider.CheckAsync(
            new AppSettings { BetterGiPath = executablePath },
            CancellationToken.None);

        Assert.Equal(testCase.UpdateAvailable, result.UpdateAvailable,
            $"版本比较结果错误：{testCase.Current} -> {testCase.Latest}");
    }
}

static async Task UnrecognizedToolVersionWarnsAndContinuesAsync()
{
    using var area = TestArea.Create();
    var executablePath = area.File("unrecognized-version.exe");
    await File.WriteAllBytesAsync(executablePath, [0x4D, 0x5A, 0x00, 0x00]);
    var provider = new VersionTestUpdateProvider("nightly-build", "2.0.0", executablePath);
    var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root));

    var result = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings
        {
            BetterGiPath = executablePath,
            UpdateToolsBeforeLaunch = true
        });

    Assert.True(result.Succeeded, "无法识别版本时应继续准备流程");
    Assert.Equal(0, result.Issues.Count, "无法识别版本不应生成任务失败记录");
    Assert.Equal(0, result.HistoryRecords.Count, "无法识别版本不应写入失败历史");
    var warning = result.Warnings.Single();
    Assert.Equal(ToolId.BetterGi, warning.ToolId, "警告应标记版本无法识别的工具");
    Assert.True(warning.Message.Contains("版本无法识别，已使用当前版本", StringComparison.Ordinal),
        "警告应说明已跳过更新并使用当前版本");
}

static async Task SessionChecksEachToolOnceAsync()
{
    using var area = TestArea.Create();
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true);
    var adapter = new PreflightAdapter(ToolId.BetterGi);
    var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root));
    var activities = new List<ToolUpdateActivity>();
    coordinator.UpdateActivityChanged += activities.Add;
    var settings = new AppSettings { UpdateToolsBeforeLaunch = true };
    var workflow = Workflow((ToolId.BetterGi, 1));

    await coordinator.PrepareAsync([adapter], workflow, settings);
    var firstActivityCount = activities.Count;
    var validations = adapter.ValidationCount;
    for (var run = 0; run < 3; run++)
    {
        settings.BetterGiProfile = $"配置 {run}";
        var result = await coordinator.PrepareAsync([adapter], workflow, settings);
        Assert.True(result.Succeeded, "取消倒计时后手动运行、继续运行均应通过本地预检");
    }

    Assert.Equal(1, provider.CheckCount, "同次打开不能再次联网检查已检查的工具");
    Assert.Equal(1, provider.UpdateCount, "同次打开不能再次调用已更新工具的更新入口");
    Assert.Equal(firstActivityCount, activities.Count, "本地验证不得再次显示更新界面");
    Assert.True(adapter.ValidationCount >= validations + 3, "每次运行都必须重新本地预检");
}

static async Task SessionChecksOnlyNewToolsAsync()
{
    using var area = TestArea.Create();
    var betterGi = new FakeToolUpdateProvider(ToolId.BetterGi);
    var maa = new FakeToolUpdateProvider(ToolId.Maa);
    var coordinator = new ToolUpdateCoordinator([betterGi, maa], new ToolUpdateStateStore(area.Root));
    var activities = new List<ToolUpdateActivity>();
    coordinator.UpdateActivityChanged += activities.Add;
    IAutomationAdapter[] adapters = [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)];
    var settings = new AppSettings { UpdateToolsBeforeLaunch = true };
    await coordinator.PrepareAsync(adapters, Workflow((ToolId.BetterGi, 1)), settings);
    Assert.Equal(0, maa.CheckCount, "未启用的 MAA 不应提前消耗检查机会");
    activities.Clear();

    await coordinator.PrepareAsync(adapters, Workflow((ToolId.Maa, 1), (ToolId.BetterGi, 2)), settings);
    Assert.Equal(1, betterGi.CheckCount, "增加工具不能使已有工具再次检查");
    Assert.Equal(1, maa.CheckCount, "新增启用工具应完成首次检查");
    Assert.SequenceEqual(["MAA"], activities.Single().Items, "界面只显示实际检查的新增工具");
    activities.Clear();
    await coordinator.PrepareAsync(adapters, Workflow((ToolId.Maa, 2)), settings);
    await coordinator.PrepareAsync(adapters, Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 1)), settings);
    Assert.Equal(1, betterGi.CheckCount, "关闭再启用不应重查");
    Assert.Equal(1, maa.CheckCount, "改通道和排序不应重查");
    Assert.Equal(0, activities.Count, "已检查工具不得闪出更新界面");
}

static async Task SessionFailedOrCancelledCheckIsNotRetriedAsync()
{
    foreach (var cancel in new[] { false, true })
    {
        using var area = TestArea.Create();
        using var cancellation = new CancellationTokenSource();
        var installationSafe = true;
        var provider = new FakeToolUpdateProvider(ToolId.BetterGi)
        {
            CheckHandler = token =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                throw new IOException("测试网络不可用");
            },
            InstallationHandler = _ => installationSafe
                ? Task.FromResult(FakeToolUpdateProvider.Fingerprint("1.0.0"))
                : throw new IOException("安装文件不可用")
        };
        var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root));
        var adapter = new PreflightAdapter(ToolId.BetterGi);
        var settings = new AppSettings { UpdateToolsBeforeLaunch = true };
        var workflow = Workflow((ToolId.BetterGi, 1));
        var first = await coordinator.PrepareAsync([adapter], workflow, settings, cancellation.Token);
        Assert.Equal(cancel, first.Cancelled, "第一次尝试应保持实际取消结果");
        var second = await coordinator.PrepareAsync([adapter], workflow, settings);
        Assert.True(second.Succeeded, "安全的当前安装仍允许手动运行");
        Assert.Equal(1, provider.CheckCount, "失败或取消也算已尝试，不能重复联网检查");
        if (!cancel)
        {
            Assert.Equal(1, second.Warnings.Count, "重复运行仍应保留未解决的更新警告");
            installationSafe = false;
            var unsafeRun = await coordinator.PrepareAsync([adapter], workflow, settings);
            Assert.False(unsafeRun.Succeeded, "缓存不能绕过失败后的安装安全检查");
            Assert.Equal(0, unsafeRun.RunnableTasks.Count, "不安全安装不能启动");
        }
    }
}

static async Task SessionPreflightFailureDoesNotConsumeCheckAsync()
{
    using var area = TestArea.Create();
    var valid = false;
    var provider = new FakeToolUpdateProvider(ToolId.Maa);
    var adapter = new PreflightAdapter(ToolId.Maa)
    {
        ValidationHandler = _ => valid ? ValidationResult.Success() : new ValidationResult(false, ["配置无效"])
    };
    var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root));
    var settings = new AppSettings { UpdateToolsBeforeLaunch = true };
    var workflow = Workflow((ToolId.Maa, 1));
    Assert.False((await coordinator.PrepareAsync([adapter], workflow, settings)).Succeeded, "无效配置不能启动");
    Assert.Equal(0, provider.CheckCount, "未开始的检查不能被记为已检查");
    valid = true;
    Assert.True((await coordinator.PrepareAsync([adapter], workflow, settings)).Succeeded, "修正配置后应检查首次更新");
    Assert.Equal(1, provider.CheckCount, "配置修正后应完成第一次检查");
    valid = false;
    var invalidAgain = await coordinator.PrepareAsync([adapter], workflow, settings);
    Assert.False(invalidAgain.Succeeded, "已检查更新不能绕过后续无效配置");
    Assert.Equal(0, invalidAgain.RunnableTasks.Count, "无效配置仍不能启动");
    Assert.Equal(1, provider.CheckCount, "不重复联网");
}

static async Task SessionCancellationKeepsUnstartedChecksAsync()
{
    using var area = TestArea.Create();
    using var cancellation = new CancellationTokenSource();
    var first = new FakeToolUpdateProvider(ToolId.BetterGi)
    {
        CheckHandler = token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(FakeToolUpdateProvider.Check(ToolId.BetterGi, false));
        }
    };
    var second = new FakeToolUpdateProvider(ToolId.Maa);
    var coordinator = new ToolUpdateCoordinator([first, second], new ToolUpdateStateStore(area.Root));
    IAutomationAdapter[] adapters = [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)];
    var settings = new AppSettings { UpdateToolsBeforeLaunch = true };
    var workflow = Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2));
    Assert.True((await coordinator.PrepareAsync(adapters, workflow, settings, cancellation.Token)).Cancelled,
        "取消应阻止整轮启动");
    Assert.Equal(0, second.CheckCount, "取消后未开始的检查应跳过");
    var manual = await coordinator.PrepareAsync(adapters, workflow, settings);
    Assert.True(manual.Succeeded, "下一次手动准备仍可完成");
    Assert.Equal(1, first.CheckCount, "已取消的检查不能重试");
    Assert.Equal(1, second.CheckCount, "未开始过的工具应获得首次检查");
}

static async Task SessionFailedRecoveryRemainsBlockedAsync()
{
    using var area = TestArea.Create();
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true)
    {
        UpdateHandler = (_, _) => Task.FromResult(ToolUpdateExecutionResult.Failure("恢复未完成", recoveryRequired: true)),
        RecoverHandler = (_, _) => Task.FromResult(ToolUpdateRecoveryResult.Failed("仍需恢复"))
    };
    var maa = new FakeToolUpdateProvider(ToolId.Maa);
    var store = new ToolUpdateStateStore(area.Root);
    var coordinator = new ToolUpdateCoordinator([provider, maa], store);
    IAutomationAdapter[] adapters = [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)];
    var settings = new AppSettings { UpdateToolsBeforeLaunch = true };
    await coordinator.PrepareAsync(adapters, Workflow((ToolId.BetterGi, 1)), settings);
    var manual = await coordinator.PrepareAsync(adapters, Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)), settings);
    Assert.False(manual.Succeeded, "已有检查记录不能掩盖更新恢复失败");
    Assert.Equal(0, manual.RunnableTasks.Count, "恢复失败阻止整轮");
    Assert.Equal(1, provider.RecoverCount, "每次准备仍处理未完成更新");
    Assert.Equal(1, provider.CheckCount, "恢复失败不能重新检查更新");
    Assert.Equal(0, maa.CheckCount, "恢复失败后不检查新增工具");
    Assert.True((await store.LoadAsync()).PendingUpdates.ContainsKey(ToolId.BetterGi), "必须保留恢复证据");
}

static async Task UpdateWarningsKeepWindowAfterSuccessfulRunAsync()
{
    foreach (var updateFailure in new[] { false, true })
    {
        using var area = TestArea.Create();
        var provider = new FakeToolUpdateProvider(ToolId.Maa, updateAvailable: updateFailure)
        {
            CheckHandler = _ => updateFailure
                ? Task.FromResult(FakeToolUpdateProvider.Check(ToolId.Maa, true))
                : throw new IOException("联网失败的技术原因"),
            UpdateHandler = (_, _) => Task.FromResult(ToolUpdateExecutionResult.Failure("下载失败的技术原因"))
        };
        var adapter = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
        var settings = new AppSettings { UpdateToolsBeforeLaunch = true, ExitAfterWorkflowCompletes = true };
        var workflow = Workflow((ToolId.Maa, 1));
        var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root));
        var preparation = await coordinator.PrepareAsync([adapter], workflow, settings);
        var records = new List<RunRecord>(preparation.HistoryRecords);
        var queue = new AutomationQueueService();
        queue.RunRecorded += records.Add;
        var result = await queue.RunAsync([adapter], preparation.RunnableTasks, settings,
            preparedWorkflowRunId: preparation.WorkflowRunId);
        Assert.Equal(QueueRunResult.AllPlannedTasksCompleted, result, "更新警告不能伪造任务失败");
        Assert.True(records.All(record => record.State == RunState.Succeeded), "成功历史保持成功");
        Assert.False(WorkflowAutomationPolicy.ShouldExitAfterCompletion(settings, result, false, true, records,
            preparation.Warnings), "更新警告必须保留窗口");
        var message = WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(result, records, false, true,
            workflow, preparation.Warnings);
        Assert.True(message is not null && message.Contains("MAA") && message.Contains("当前版本"), "结束时应提醒更新失败");
        Assert.True(message!.Length < 50 && !message.Contains("技术原因"), "提醒只显示简短信息");
        Assert.True(WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(result, records, true, true,
            workflow, preparation.Warnings) is null, "关闭应用时不能反向弹窗");
        var repeated = await coordinator.PrepareAsync([new PreflightAdapter(ToolId.Maa)], workflow, settings);
        Assert.Equal(1, repeated.Warnings.Count, "后续手动运行仍保留未解决的更新警告");
        Assert.Equal(1, provider.CheckCount, "提醒不能触发重新检查");
        Assert.Equal(updateFailure ? 1 : 0, provider.UpdateCount, "提醒不能触发重新更新");
    }
}

static Task CompletionReminderIsConciseAsync()
{
    var record = new RunRecord
    {
        ToolId = ToolId.Maa, ToolName = "MAA", State = RunState.Failed,
        StartedAt = DateTimeOffset.UnixEpoch, EndedAt = DateTimeOffset.UnixEpoch,
        Message = "底层异常：" + new string('错', 2000)
    };
    var message = WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(
        QueueRunResult.NotAllPlannedTasksCompleted, [record], false, false);
    Assert.True(message is not null && message.Contains("MAA") && message.Contains("历史保存失败"),
        "提醒应保留工具和用户需要处理的状态");
    Assert.True(message!.Length < 100 && !message.Contains("底层异常"), "弹窗不能堆砌底层长篇错误");
    Assert.True(record.Message.Length > 2000, "展示简化不能破坏原始诊断信息");
    return Task.CompletedTask;
}

static async Task ToolUpdateActivityReportsStagesAndItemsAsync()
{
    using var area = TestArea.Create();
    var betterGi = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true);
    var maa = new FakeToolUpdateProvider(ToolId.Maa);
    var coordinator = new ToolUpdateCoordinator(
        [betterGi, maa],
        new ToolUpdateStateStore(area.Root));
    var activities = new ConcurrentQueue<ToolUpdateActivity>();
    coordinator.UpdateActivityChanged += activities.Enqueue;

    var result = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)),
        new AppSettings { UpdateToolsBeforeLaunch = true });

    Assert.True(result.Succeeded, "带更新活动报告的准备流程应成功");
    var captured = activities.ToArray();
    var checking = captured.First(activity => activity.Phase == ToolUpdateActivityPhase.Checking);
    var updating = captured.First(activity => activity.Phase == ToolUpdateActivityPhase.Updating);
    Assert.SequenceEqual(["BetterGI", "MAA"], checking.Items,
        "检查阶段应列出全部已启用工具");
    Assert.SequenceEqual(["BetterGI"], updating.Items,
        "更新阶段只应列出实际需要更新的项目");
    Assert.SequenceEqual(["BetterGI"], result.UpdatedItems,
        "准备结果应返回本轮实际完成的更新项目");
}

static async Task SessionToolSettingsDoNotRepeatChecksAsync()
{
    (ToolId ToolId, Action<AppSettings> Mutate, string Name)[] cases =
    [
        (ToolId.BetterGi, settings => settings.BetterGiPath += ".changed", "BetterGI 路径"),
        (ToolId.BetterGi, settings => settings.BetterGiMode = "ScriptGroups", "BetterGI 模式"),
        (ToolId.BetterGi, settings => settings.BetterGiProfile = "周常", "BetterGI 配置"),
        (ToolId.Maa, settings => settings.MaaPath += ".changed", "MAA 路径"),
        (ToolId.Maa, settings => settings.MaaProfile = "Alternate", "MAA 配置"),
        (ToolId.MaaEnd, settings => settings.MaaEndPath += ".changed", "MaaEnd 路径"),
        (ToolId.MaaEnd, settings => settings.MaaEndInstance = "周常实例", "MaaEnd 实例")
    ];

    foreach (var testCase in cases)
    {
        using var area = TestArea.Create();
        var provider = new FakeToolUpdateProvider(testCase.ToolId);
        var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root));
        var settings = CreateSessionCheckSettings();
        var workflow = Workflow((testCase.ToolId, 1));

        var first = await coordinator.PrepareAsync(
            [new PreflightAdapter(testCase.ToolId)], workflow, settings);
        testCase.Mutate(settings);
        var second = await coordinator.PrepareAsync(
            [new PreflightAdapter(testCase.ToolId)], workflow, settings,
            CancellationToken.None);

        Assert.True(second.Succeeded, $"{testCase.Name}变化后的完整准备应成功");
        Assert.Equal(1, provider.CheckCount, $"{testCase.Name}变化后仍只做本地预检");
    }
}

static async Task SessionUpdateToggleDoesNotRepeatChecksAsync()
{
    using var area = TestArea.Create();
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi);
    var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root));
    var settings = CreateSessionCheckSettings();
    var workflow = Workflow((ToolId.BetterGi, 1));

    settings.UpdateToolsBeforeLaunch = false;
    await coordinator.PrepareAsync([new PreflightAdapter(ToolId.BetterGi)], workflow, settings);
    Assert.Equal(0, provider.CheckCount, "关闭自动更新时不消耗首次检查机会");
    settings.UpdateToolsBeforeLaunch = true;
    var first = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)], workflow, settings);
    settings.UpdateToolsBeforeLaunch = false;
    var disabled = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)], workflow, settings,
        CancellationToken.None);
    settings.UpdateToolsBeforeLaunch = true;
    var enabledAgain = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)], workflow, settings,
        CancellationToken.None);

    Assert.True(disabled.Succeeded && enabledAgain.Succeeded,
        "自动更新设置变化时准备流程仍应安全成功");
    Assert.Equal(1, provider.CheckCount,
        "关闭再开启自动更新不应重查已检查的工具");
}

static async Task NewCoordinatorChecksToolsAgainAsync()
{
    using var area = TestArea.Create();
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi);
    var store = new ToolUpdateStateStore(area.Root);
    var issuer = new ToolUpdateCoordinator([provider], store);
    var other = new ToolUpdateCoordinator([provider], store);
    var settings = CreateSessionCheckSettings();
    var workflow = Workflow((ToolId.BetterGi, 1));

    var first = await issuer.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)], workflow, settings);
    var foreignAttempt = await other.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)], workflow, settings,
        CancellationToken.None);
    var reusedAfterForeignAttempt = await issuer.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)], workflow, settings,
        CancellationToken.None);

    Assert.True(foreignAttempt.Succeeded && reusedAfterForeignAttempt.Succeeded,
        "新的会话及原会话均应安全准备");
    Assert.Equal(2, provider.CheckCount, "新应用会话应重新检查，原会话仍不重复检查");
}

static async Task SessionPendingUpdateStillRecoversAsync()
{
    using var area = TestArea.Create();
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi);
    var store = new ToolUpdateStateStore(area.Root);
    var coordinator = new ToolUpdateCoordinator([provider], store);
    var settings = CreateSessionCheckSettings();
    var workflow = Workflow((ToolId.BetterGi, 1));

    var first = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)], workflow, settings);
    await store.SaveAsync(new ToolUpdatePersistentState
    {
        PendingUpdates =
        {
            [ToolId.BetterGi] = new ToolUpdatePendingState
            {
                ToolId = ToolId.BetterGi,
                TargetVersion = "2.0.0",
                BeforeFingerprint = FakeToolUpdateProvider.Fingerprint("1.0.0"),
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            }
        }
    });

    var second = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)], workflow, settings,
        CancellationToken.None);

    Assert.True(second.Succeeded, "pending 恢复后本地准备应成功");
    Assert.Equal(1, provider.RecoverCount, "存在 pending 时应优先执行更新恢复");
    Assert.Equal(1, provider.CheckCount, "恢复完成不应重新联网检查本会话已经检查过的工具");
}

static async Task ConcurrentPreparationFailsFastAsync()
{
    using var area = TestArea.Create();
    var releaseUpdate = NewGate();
    var betterGi = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true)
    {
        UpdateHandler = async (_, _) =>
        {
            await releaseUpdate.Task;
            return FakeToolUpdateProvider.Success(ToolId.BetterGi, "2.0.0");
        }
    };
    var maa = new FakeToolUpdateProvider(ToolId.Maa);
    var store = new ToolUpdateStateStore(area.Root);
    var coordinator = new ToolUpdateCoordinator([betterGi, maa], store);
    var firstPreparation = coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings { UpdateToolsBeforeLaunch = true });

    await betterGi.UpdateStarted.Task;
    InvalidOperationException? rejection = null;
    try
    {
        var initialState = await store.LoadAsync();
        Assert.True(initialState.PendingUpdates.ContainsKey(ToolId.BetterGi),
            "第一轮更新启动前应先保存 BetterGI pending state");

        try
        {
            await coordinator.PrepareAsync(
                [new PreflightAdapter(ToolId.Maa)],
                Workflow((ToolId.Maa, 1)),
                new AppSettings { UpdateToolsBeforeLaunch = true });
        }
        catch (InvalidOperationException exception)
        {
            rejection = exception;
        }

        var state = await store.LoadAsync();
        Assert.True(rejection is not null,
            $"第二轮启动准备应快速拒绝；实际恢复次数：{betterGi.RecoverCount}，MAA 检查次数：{maa.CheckCount}，BetterGI pending：{state.PendingUpdates.ContainsKey(ToolId.BetterGi)}");
        Assert.Equal("已有启动准备正在运行。", rejection!.Message, "并发准备拒绝原因不正确");
        Assert.Equal(0, betterGi.RecoverCount, "第二轮不得把第一轮活跃 pending 当作遗留更新恢复");
        Assert.True(state.PendingUpdates.ContainsKey(ToolId.BetterGi),
            "第二轮不得覆盖第一轮尚未完成的 BetterGI pending state");
        Assert.Equal(0, maa.CheckCount, "并发准备拒绝前不得进入第二轮 provider 检查");
    }
    finally
    {
        releaseUpdate.TrySetResult();
        await firstPreparation;
    }
}

static AppSettings CreateSessionCheckSettings() => new()
{
    BetterGiPath = @"C:\Tools\BetterGI\BetterGI.exe",
    BetterGiMode = "OneDragon",
    BetterGiProfile = "日常",
    MaaPath = @"C:\Tools\MAA\MAA.exe",
    MaaProfile = "Default",
    MaaEndPath = @"C:\Tools\MaaEnd\MaaEnd.exe",
    MaaEndInstance = "快速日常",
    UpdateToolsBeforeLaunch = true
};

static async Task ToolUpdateCheckBarrierAsync()
{
    using var area = TestArea.Create();
    var releaseSecondCheck = NewGate();
    var first = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true);
    var second = new FakeToolUpdateProvider(ToolId.Maa)
    {
        CheckHandler = async cancellationToken =>
        {
            await releaseSecondCheck.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("MAA 版本服务不可用");
        }
    };
    var coordinator = new ToolUpdateCoordinator([first, second], new ToolUpdateStateStore(area.Root));
    var run = coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)),
        new AppSettings { UpdateToolsBeforeLaunch = true });

    await second.CheckStarted.Task;
    await Task.Delay(50);
    Assert.Equal(0, first.UpdateCount, "全部版本检查结束前不得启动任何更新");
    releaseSecondCheck.SetResult();
    var result = await run;

    Assert.True(result.Succeeded, "纯版本检查失败不应阻止准备流程");
    Assert.Equal(1, first.UpdateCount, "检查屏障结束后应继续已经确认可用的更新");
    Assert.Equal(0, result.Issues.Count, "纯版本检查失败不应生成任务失败记录");
    var warning = result.Warnings.Single();
    Assert.Equal(ToolId.Maa, warning.ToolId, "警告应标记检查失败的具体工具");
    Assert.Equal("更新检查失败，已使用当前版本", warning.Message,
        "检查失败应使用简短且不阻断的用户提示");
    Assert.True(warning.Detail?.Contains("MAA 版本服务不可用", StringComparison.Ordinal) == true,
        "技术日志应保留可诊断的检查失败原因");
}

static async Task ToolUpdatesRunInParallelAsync()
{
    using var area = TestArea.Create();
    var releaseUpdates = NewGate();
    var first = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true)
    {
        UpdateHandler = async (_, _) =>
        {
            await releaseUpdates.Task;
            return FakeToolUpdateProvider.Success(ToolId.BetterGi, "2.0.0");
        }
    };
    var second = new FakeToolUpdateProvider(ToolId.Maa, updateAvailable: true)
    {
        UpdateHandler = async (_, _) =>
        {
            await releaseUpdates.Task;
            return FakeToolUpdateProvider.Success(ToolId.Maa, "2.0.0");
        }
    };
    var coordinator = new ToolUpdateCoordinator([first, second], new ToolUpdateStateStore(area.Root));
    var run = coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)),
        new AppSettings { UpdateToolsBeforeLaunch = true });

    await Task.WhenAll(first.UpdateStarted.Task, second.UpdateStarted.Task);
    Assert.Equal(1, first.UpdateCount, "BetterGI 更新未启动");
    Assert.Equal(1, second.UpdateCount, "MAA 更新未与 BetterGI 并行启动");
    releaseUpdates.SetResult();

    Assert.True((await run).Succeeded, "两个并行更新正常结束后应继续工作流");
}

static async Task ExternalUpdateCancellationAsync(ToolId toolId, bool selfUpdating)
{
    using var area = TestArea.Create();
    var executablePath = CreateCloseWindowExecutable(area);
    var provider = new OwnershipTestUpdateProvider(executablePath)
    {
        TestToolId = toolId,
        SelfUpdating = selfUpdating,
        WaitForUpdateProcessExit = selfUpdating
    };
    var store = new ToolUpdateStateStore(area.Root);
    var coordinator = new ToolUpdateCoordinator([provider], store);
    var settings = new AppSettings { UpdateToolsBeforeLaunch = true };
    var workflow = Workflow((toolId, 1));
    using var cancellation = new CancellationTokenSource();
    var run = coordinator.PrepareAsync([new PreflightAdapter(toolId)], workflow, settings, cancellation.Token);
    Task<ToolPreparationResult>? recovery = null;
    Process? process = null;
    try
    {
        var ready = await Task.WhenAny(provider.ProcessRecorded.Task, run).WaitAsync(TimeSpan.FromSeconds(5));
        if (ready == run)
        {
            throw new InvalidOperationException(
                $"更新在进程状态保存前结束：{string.Join(" | ", (await run).Issues.Select(issue => issue.Message))}");
        }
        process = Process.GetProcessById(await provider.ProcessRecorded.Task);
        await WaitForMainWindowAsync(process);

        cancellation.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Cancelled, "停止等待必须返回取消状态");
        Assert.Equal(0, result.RunnableTasks.Count, "取消后不得启动游戏任务");
        Assert.False(process.HasExited, "取消不得结束外部更新进程");
        Assert.Equal((int?)process.Id, (await store.LoadAsync()).PendingUpdates[toolId].ProcessId,
            "取消后保留更新进程和安装恢复证据");
        Assert.True(result.Warnings.Any(warning => warning.ToolId == toolId),
            "取消结果必须说明外部更新尚需确认");

        using var recoveryCancellation = new CancellationTokenSource();
        var recoveryStarted = NewGate();
        provider.ReadVersionCallback = _ => recoveryStarted.TrySetResult();
        recovery = coordinator.PrepareAsync([new PreflightAdapter(toolId)], workflow, settings,
            recoveryCancellation.Token);
        await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        recoveryCancellation.Cancel();
        var recoveryResult = await recovery.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(recoveryResult.Cancelled, "再次启动的恢复等待也必须可取消");
        Assert.False(process.HasExited, "取消恢复不得关闭仍在更新的进程");
        Assert.True((await store.LoadAsync()).PendingUpdates.ContainsKey(toolId), "取消恢复不能清除证据");
        Assert.True(recoveryResult.HistoryRecords.All(record => record.State == RunState.Cancelled),
            "主动取消恢复不能记为工具失败");

        provider.ReadVersionCallback = null;
        _ = process.CloseMainWindow();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var resumed = await coordinator.PrepareAsync([new PreflightAdapter(toolId)], workflow, settings);
        Assert.Equal(1, resumed.RunnableTasks.Count, "外部进程退出且旧安装未变后可恢复使用");
        Assert.Equal(0, (await store.LoadAsync()).PendingUpdates.Count, "核验旧安装安全后清除待恢复记录");
    }
    finally
    {
        await EnsureCloseWindowProcessesExitedAsync(executablePath);
        await run;
        if (recovery is not null) await recovery;
        process?.Dispose();
    }
}

static async Task ToolUpdateCancellationWaitsForStartedUpdatesAsync()
{
    using var area = TestArea.Create();
    using var cancellation = new CancellationTokenSource();
    var releaseUpdates = NewGate();
    var first = CreateBlockingUpdateProvider(ToolId.BetterGi, releaseUpdates.Task);
    var second = CreateBlockingUpdateProvider(ToolId.Maa, releaseUpdates.Task);
    var coordinator = new ToolUpdateCoordinator([first, second], new ToolUpdateStateStore(area.Root));
    var run = coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)),
        new AppSettings { UpdateToolsBeforeLaunch = true },
        cancellation.Token);

    await Task.WhenAll(first.UpdateStarted.Task, second.UpdateStarted.Task);
    cancellation.Cancel();
    await Task.Delay(50);
    Assert.False(run.IsCompleted, "取消后仍应等待已经启动的官方更新安全收尾");
    releaseUpdates.SetResult();
    var result = await run;

    Assert.True(result.Cancelled, "安全收尾后应以取消状态阻止工作流");
    Assert.Equal(2, result.Issues.Count, "取消结果应为全部计划工具返回状态");
    Assert.True(result.Issues.All(issue => issue.Message == "启动准备已取消"),
        "取消状态应只保留必要的简短说明");
    Assert.Equal(2, result.HistoryRecords.Count, "取消结果应为全部计划工具写入工作流历史");
    Assert.True(result.HistoryRecords.All(record => record.State == RunState.Cancelled),
        "取消历史状态不正确");
    Assert.True(result.HistoryRecords.All(record => record.Message == "启动准备已取消"),
        "取消历史应只保留必要的简短说明");
}

static async Task UpdateStateSaveFailureRetainsEvidenceAsync()
{
    using var area = TestArea.Create();
    var store = new ToolUpdateStateStore(area.Root);
    var updating = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true)
    {
        UpdateHandler = (_, _) =>
        {
            Directory.CreateDirectory(store.StatePath + ".tmp");
            return Task.FromResult(FakeToolUpdateProvider.Success(ToolId.BetterGi, "2.0.0"));
        }
    };
    var result = await new ToolUpdateCoordinator([updating, new FakeToolUpdateProvider(ToolId.Maa)], store)
        .PrepareAsync([new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)],
            Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)), new AppSettings { UpdateToolsBeforeLaunch = true });
    Assert.Equal(0, result.RunnableTasks.Count, "更新状态保存失败阻止整轮");
    Assert.True(result.Issues[0].Message.Contains("更新结果无法保存"), "记录全局存储失败原因");
    Assert.True((await store.LoadAsync()).PendingUpdates.ContainsKey(ToolId.BetterGi), "磁盘恢复点仍在");
}

static async Task UncertainInstallationsAreExcludedAsync()
{
    foreach (var failureKind in new[] { "pending", "changed", "invalid-data", "win32", "check-invalid-data" })
    {
        using var area = TestArea.Create();
        var store = new ToolUpdateStateStore(area.Root);
        var provider = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true)
        {
            CheckHandler = failureKind == "check-invalid-data"
                ? _ => throw new HttpRequestException("联网失败") : null,
            UpdateHandler = (_, _) => Task.FromResult(ToolUpdateExecutionResult.Failure("下载或更新失败",
                recoveryRequired: failureKind == "pending")),
            InstallationHandler = _ => failureKind switch
            {
                "invalid-data" or "check-invalid-data" => throw new InvalidDataException("安装状态损坏"),
                "win32" => throw new System.ComponentModel.Win32Exception("进程访问失败"),
                "changed" => Task.FromResult(FakeToolUpdateProvider.Fingerprint("unknown")),
                _ => Task.FromResult(FakeToolUpdateProvider.Fingerprint("1.0.0"))
            }
        };
        var first = new FakeAdapter(ToolId.BetterGi, RunState.Succeeded);
        var second = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
        var workflow = Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2));
        var settings = new AppSettings { UpdateToolsBeforeLaunch = true, ExitAfterWorkflowCompletes = true };
        var result = await new ToolUpdateCoordinator([provider, new FakeToolUpdateProvider(ToolId.Maa)], store)
            .PrepareAsync([first, second], workflow, settings);
        Assert.False(result.Succeeded, "不确定安装不能宣告全部成功");
        Assert.Equal(0, result.RunnableTasks.Count, "不安全安装阻止整轮");
        var records = new List<RunRecord>(result.HistoryRecords);
        var queue = new AutomationQueueService();
        queue.RunRecorded += records.Add;
        var queueResult = await queue.RunAsync([first, second], result.RunnableTasks, settings,
            preparedWorkflowRunId: result.WorkflowRunId);
        Assert.Equal(0, first.StartCount, "不启动不安全工具");
        Assert.Equal(0, second.StartCount, "其他工具也不启动");
        Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, queueResult, "整轮未启动");
        Assert.True(records.All(record => record.WorkflowRunId == result.WorkflowRunId), "准备和队列保留同轮身份");
        Assert.Equal(result.Issues[0].TaskExecutionId, result.HistoryRecords[0].TaskExecutionId, "失败任务身份一致");
        Assert.False(WorkflowAutomationPolicy.ShouldExitAfterCompletion(settings, queueResult, false, true, records),
            "剩余成功不能掩盖准备失败而退出");
        var message = WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(queueResult, records, false, true, workflow);
        Assert.True(message is not null && message.Contains("BetterGI"), "结束提醒包含被排除工具");
        if (failureKind != "check-invalid-data")
            Assert.True((await store.LoadAsync()).PendingUpdates.ContainsKey(ToolId.BetterGi), "不能清掉不确定安装证据");
    }
}

static async Task AllPreparationFailuresRetainHistoryAsync()
{
    using var area = TestArea.Create();
    var providers = new[] { ToolId.BetterGi, ToolId.Maa }.Select(id => new FakeToolUpdateProvider(id)
    {
        Validation = new ValidationResult(false, ["本地配置无效"])
    }).ToArray();
    var workflow = Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2));
    var result = await new ToolUpdateCoordinator(providers, new ToolUpdateStateStore(area.Root)).PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)], workflow,
        new AppSettings { UpdateToolsBeforeLaunch = true });
    Assert.Equal(0, result.RunnableTasks.Count, "全部不可用时没有启动任务");
    Assert.Equal(2, result.HistoryRecords.Count, "保存所有失败");
    Assert.True(providers.All(provider => provider.CheckCount == 0), "无效工具不检查更新");
    var message = WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(
        QueueRunResult.NotAllPlannedTasksCompleted, result.HistoryRecords, false, true, workflow);
    Assert.True(message is not null && message.Contains("BetterGI") && message.Contains("MAA"), "一次提醒全部工具");
    var missing = WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(
        QueueRunResult.NotAllPlannedTasksCompleted, [], false, true, workflow);
    Assert.True(missing!.Contains("BetterGI：未运行") && missing.Contains("MAA：未运行"), "无运行记录也不漏未运行工具");
}

static async Task PreparationCancellationRetainsFailuresAsync()
{
    using var area = TestArea.Create();
    using var cancellation = new CancellationTokenSource();
    var store = new ToolUpdateStateStore(area.Root);
    var invalid = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true)
    {
        UpdateHandler = (_, _) => Task.FromResult(ToolUpdateExecutionResult.Failure("更新失败", recoveryRequired: true))
    };
    var cancelling = new FakeToolUpdateProvider(ToolId.Maa, updateAvailable: true)
    {
        UpdateHandler = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(ToolUpdateExecutionResult.CancelledResult("更新已取消"));
        },
        InstallationHandler = _ => throw new InvalidDataException("收尾状态不确定")
    };
    var result = await new ToolUpdateCoordinator([invalid, cancelling], store).PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)), new AppSettings { UpdateToolsBeforeLaunch = true }, cancellation.Token);
    Assert.True(result.Cancelled, "取消整轮");
    Assert.Equal(0, result.RunnableTasks.Count, "取消后不得启动任务");
    Assert.Equal(2, result.HistoryRecords.Count, "先前失败和取消均保留");
    Assert.True(result.HistoryRecords.Any(record => record.ToolId == ToolId.BetterGi && record.State == RunState.Failed), "不覆盖已知失败");
    Assert.True((await store.LoadAsync()).PendingUpdates.ContainsKey(ToolId.Maa), "不确定取消收尾保留恢复证据");
}

static async Task RunnablePreparationUsesSnapshotAsync()
{
    using var area = TestArea.Create();
    var result = await new ToolUpdateCoordinator([], new ToolUpdateStateStore(area.Root)).PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)],
        [null!, new() { ToolId = ToolId.Maa, Channel = -1 }, new() { ToolId = ToolId.BetterGi, Channel = 99 },
            new() { ToolId = ToolId.Maa, Channel = 2 }], new AppSettings());
    Assert.Equal(2, result.RunnableTasks.Count, "去掉重复和空项");
    Assert.Equal(ToolId.Maa, result.RunnableTasks[0].ToolId, "保留计划顺序");
    Assert.Equal(1, result.RunnableTasks[0].Channel, "保留规范化通道");
    Assert.Equal(WorkflowTaskPlan.CreateSnapshot([new() { ToolId = ToolId.BetterGi, Channel = 99 }])[0].Channel,
        result.RunnableTasks[1].Channel, "沿用项目通道边界");
}

static async Task NullPendingUpdatesBlocksPreparationAsync()
{
    using var area = TestArea.Create();
    var store = new ToolUpdateStateStore(area.Root);
    await File.WriteAllTextAsync(store.StatePath, "{\"SchemaVersion\":1,\"PendingUpdates\":null}");
    var result = await new ToolUpdateCoordinator([], store).PrepareAsync([new PreflightAdapter(ToolId.BetterGi)],
        Workflow((ToolId.BetterGi, 1)), new AppSettings());
    Assert.False(result.Succeeded, "状态损坏不能视为安全");
    Assert.Equal(0, result.RunnableTasks.Count, "损坏状态禁止启动");
    Assert.True((await File.ReadAllTextAsync(store.StatePath)).Contains("null"), "保留损坏证据");
}

static async Task SafeUpdateFailureFallsBackAsync()
{
    using var area = TestArea.Create();
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true)
    {
        UpdateHandler = (_, _) => Task.FromResult(ToolUpdateExecutionResult.Failure("下载失败"))
    };
    var result = await new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root))
        .PrepareAsync([new PreflightAdapter(ToolId.BetterGi)], Workflow((ToolId.BetterGi, 1)),
            new AppSettings { UpdateToolsBeforeLaunch = true });
    Assert.True(result.Succeeded, "已确认安全的旧安装应可运行");
    Assert.Equal(0, result.HistoryRecords.Count, "更新警告不能伪造任务失败");
    Assert.True(result.Warnings.Any(warning => warning.Detail!.Contains("下载失败")), "保留更新原因");
    Assert.Equal(1, provider.UpdateCount, "不自动重试");
}

static async Task RecoveryFailureIsolatedAsync()
{
    using var area = TestArea.Create();
    var store = new ToolUpdateStateStore(area.Root);
    await store.SaveAsync(new ToolUpdatePersistentState
    {
        PendingUpdates = new()
        {
            [ToolId.BetterGi] = new ToolUpdatePendingState
            {
                ToolId = ToolId.BetterGi, TargetVersion = "2.0.0",
                BeforeFingerprint = FakeToolUpdateProvider.Fingerprint("1.0.0"),
                StartedAt = DateTimeOffset.UnixEpoch
            }
        }
    });
    var failed = new FakeToolUpdateProvider(ToolId.BetterGi)
    {
        RecoverHandler = (_, _) => Task.FromResult(ToolUpdateRecoveryResult.Failed("恢复未完成"))
    };
    var sibling = new FakeToolUpdateProvider(ToolId.Maa);
    var result = await new ToolUpdateCoordinator([failed, sibling], store).PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)), new AppSettings { UpdateToolsBeforeLaunch = true });
    Assert.Equal(0, sibling.CheckCount, "恢复失败后不检查其他工具");
    Assert.Equal(0, failed.CheckCount, "恢复失败工具不能联网重试");
    Assert.True((await store.LoadAsync()).PendingUpdates.ContainsKey(ToolId.BetterGi), "保留恢复证据");
    Assert.Equal(2, result.HistoryRecords.Count, "失败与跳过记录不能丢失");
}

static Task IncompleteWorkflowReminderAsync()
{
    var record = new RunRecord
    {
        ToolId = ToolId.BetterGi, ToolName = "BetterGI", State = RunState.Failed,
        Message = "恢复未完成", StartedAt = DateTimeOffset.UnixEpoch, EndedAt = DateTimeOffset.UnixEpoch
    };
    var message = WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(
        QueueRunResult.NotAllPlannedTasksCompleted, [record], false, false);
    Assert.True(message is not null && message.Contains("BetterGI：运行失败") && message.Contains("历史保存失败"),
        "整轮结束提醒应包含未完成工具和历史失败");
    Assert.True(WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(
        QueueRunResult.NotAllPlannedTasksCompleted, [record], true, false) is null, "关闭不弹窗");
    return Task.CompletedTask;
}

static async Task ToolUpdateFailureWaitsForSiblingsAsync()
{
    using var area = TestArea.Create();
    var releaseSibling = NewGate();
    var failed = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true)
    {
        UpdateHandler = (_, _) => Task.FromResult(
            ToolUpdateExecutionResult.Failure("BetterGI 官方更新失败", recoveryRequired: true))
    };
    var sibling = CreateBlockingUpdateProvider(ToolId.Maa, releaseSibling.Task);
    var coordinator = new ToolUpdateCoordinator([failed, sibling], new ToolUpdateStateStore(area.Root));
    var run = coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)),
        new AppSettings { UpdateToolsBeforeLaunch = true });

    await Task.WhenAll(failed.UpdateStarted.Task, sibling.UpdateStarted.Task);
    Assert.False(run.IsCompleted, "一个更新失败后仍应等待其他已启动更新完成");
    releaseSibling.SetResult();
    var result = await run;

    Assert.False(result.Succeeded, "任一更新失败应阻止工作流");
    Assert.True(result.Issues.Any(issue => issue.ToolId == ToolId.BetterGi), "失败结果应标记具体工具");
    Assert.True(result.HistoryRecords.Any(record => record.ToolId == ToolId.BetterGi
        && record.State == RunState.Failed), "更新失败应写入工作流历史");
}

static async Task PendingToolUpdateRecoversBeforeChecksAsync()
{
    using var area = TestArea.Create();
    var recovered = false;
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi)
    {
        RecoverHandler = (_, _) =>
        {
            recovered = true;
            return Task.FromResult(ToolUpdateRecoveryResult.Completed(
                "恢复完成",
                FakeToolUpdateProvider.Fingerprint("2.0.0")));
        },
        CheckHandler = _ =>
        {
            Assert.True(recovered, "版本检查必须发生在未完成更新恢复之后");
            return Task.FromResult(FakeToolUpdateProvider.Check(ToolId.BetterGi, false));
        }
    };
    var store = new ToolUpdateStateStore(area.Root);
    await store.SaveAsync(new ToolUpdatePersistentState
    {
        PendingUpdates =
        {
            [ToolId.BetterGi] = new ToolUpdatePendingState
            {
                ToolId = ToolId.BetterGi,
                TargetVersion = "2.0.0",
                BeforeFingerprint = FakeToolUpdateProvider.Fingerprint("1.0.0"),
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            }
        }
    });
    var coordinator = new ToolUpdateCoordinator([provider], store);

    var result = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings { UpdateToolsBeforeLaunch = true });
    var state = await store.LoadAsync();

    Assert.True(result.Succeeded, "可确认完成的遗留更新应恢复并继续准备流程");
    Assert.Equal(1, provider.RecoverCount, "遗留更新未执行恢复");
    Assert.Equal(0, state.PendingUpdates.Count, "恢复成功后不应保留 pending 状态");
    var persisted = await File.ReadAllTextAsync(store.StatePath);
    Assert.False(persisted.Contains("TrustedFingerprints", StringComparison.Ordinal),
        "恢复状态保存后不应继续写入旧可信指纹字段");
}

static async Task LegacyTrustedFingerprintDoesNotBlockPreparationAsync()
{
    using var area = TestArea.Create();
    var store = new ToolUpdateStateStore(area.Root);
    Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
    await File.WriteAllTextAsync(store.StatePath, """
        {
          "SchemaVersion": 1,
          "TrustedFingerprints": {
            "BetterGi": {
              "Version": "1.0.0",
              "Sha256": "OLD-HASH",
              "TotalLength": 10,
              "CapturedAt": "2026-08-26T00:00:00+00:00"
            }
          },
          "PendingUpdates": {}
        }
        """);
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi)
    {
        CheckHandler = _ => Task.FromResult(new ToolUpdateCheckResult(
            ToolId.BetterGi,
            new ToolVersionFingerprint("1.0.0", "NEW-HASH", 10, DateTimeOffset.UtcNow),
            "1.0.0",
            false))
    };
    var coordinator = new ToolUpdateCoordinator([provider], store);

    var result = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings { UpdateToolsBeforeLaunch = true });

    Assert.True(result.Succeeded, "同版本文件变化不应被旧可信指纹阻止");
    Assert.Equal(0, result.Issues.Count, "旧可信指纹不应生成启动准备失败");
}

static async Task PostUpdatePreflightFailureBlocksAsync()
{
    using var area = TestArea.Create();
    var adapter = new PreflightAdapter(ToolId.BetterGi)
    {
        ValidationHandler = count => count == 1
            ? ValidationResult.Success()
            : new ValidationResult(false, ["更新后程序入口失效"])
    };
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi);
    var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.Root));

    var result = await coordinator.PrepareAsync(
        [adapter],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings { UpdateToolsBeforeLaunch = true });

    Assert.False(result.Succeeded, "更新后二次预检失败应阻止工作流");
    Assert.Equal(2, adapter.ValidationCount, "准备流程应执行更新前和更新后二次预检");
    Assert.True(result.Issues.Single().Message.Contains("更新后二次预检失败", StringComparison.Ordinal),
        "二次预检错误应有明确标记");
}

static Task ToolUpdateProvidersUseExpectedArgumentsAsync()
{
    var settings = new AppSettings
    {
        BetterGiPath = @"C:\Tools BetterGI\BetterGI.exe",
        MaaPath = @"C:\Tools MAA\MAA.exe",
        MaaProfile = "中文 配置",
        MaaEndPath = @"C:\Tools MaaEnd\MaaEnd.exe"
    };

    var betterGi = new BetterGiUpdateProvider().BuildUpdateStartInfo(settings);
    Assert.Equal(@"C:\Tools BetterGI\BetterGI.update.exe", betterGi.FileName,
        "BetterGI 应通过官方 Kachina 更新入口启动");
    Assert.SequenceEqual(["-I", "-S", "--source", "cnb"], betterGi.ArgumentList,
        "BetterGI 官方更新参数不正确");

    var maa = new MaaUpdateProvider().BuildUpdateStartInfo(settings);
    Assert.Equal(settings.MaaPath, maa.FileName, "MAA 应通过自身内置更新入口启动");
    Assert.SequenceEqual(["--config", "中文 配置", "--skip-startup-auto-run"], maa.ArgumentList,
        "MAA 更新启动参数不正确");

    var maaEnd = new MaaEndUpdateProvider().BuildUpdateStartInfo(settings);
    Assert.Equal(settings.MaaEndPath, maaEnd.FileName, "MaaEnd 应通过 MXU 自身内置更新入口启动");
    Assert.Equal(0, maaEnd.ArgumentList.Count, "MaaEnd 更新检查不应附带任务自动运行参数");
    return Task.CompletedTask;
}

static async Task ToolUpdateClosesProcessAfterUpdateAsync()
{
    using var area = TestArea.Create();
    var executablePath = CreateCloseWindowExecutable(area);
    var provider = new OwnershipTestUpdateProvider(executablePath);
    Process? updateProcess = null;
    string? persistedProcessPath = null;
    var context = new ToolUpdateExecutionContext(
        async (processId, processPath, _) =>
        {
            persistedProcessPath = processPath;
            updateProcess = Process.GetProcessById(processId);
            await WaitForMainWindowAsync(updateProcess);
            provider.CurrentVersion = "2.0.0";
        },
        (_, _) => Task.CompletedTask);

    try
    {
        var result = await provider.UpdateAsync(
            new AppSettings(),
            new ToolUpdateCheckResult(
                ToolId.BetterGi,
                FakeToolUpdateProvider.Fingerprint("1.0.0"),
                "2.0.0",
                true),
            context,
            CancellationToken.None);

        Assert.True(result.Succeeded, $"更新应完成：{result.Message}");
        Assert.Equal(Path.GetFullPath(executablePath), persistedProcessPath,
            "更新状态应保存进程解析后的实际路径");
        Assert.True(updateProcess is not null && updateProcess.HasExited,
            "正常更新完成后自更新程序必须被正常关闭");
    }
    finally
    {
        updateProcess?.Dispose();
        await EnsureCloseWindowProcessesExitedAsync(executablePath);
    }
}
static async Task ToolUpdateRecoveryClosesProcessAsync()
{
    using var area = TestArea.Create();
    var executablePath = CreateCloseWindowExecutable(area);
    using var updateProcess = StartCloseWindowProcess(executablePath);
    try
    {
        await WaitForMainWindowAsync(updateProcess);
        var updateStartedAt = new DateTimeOffset(updateProcess.StartTime.ToUniversalTime());
        var resolvedPath = Path.GetFullPath(updateProcess.MainModule?.FileName
            ?? throw new InvalidOperationException("无法取得事务测试进程实际路径"));
        var pending = JsonSerializer.Deserialize<ToolUpdatePendingState>($$"""
            {
              "ToolId": 0,
              "TargetVersion": "2.0.0",
              "BeforeFingerprint": {
                "Version": "1.0.0",
                "Sha256": "HASH-1.0.0",
                "TotalLength": 5,
                "CapturedAt": "2026-08-30T00:00:00+00:00"
              },
              "StartedAt": "{{updateStartedAt:O}}",
              "ProcessId": {{updateProcess.Id}},
              "ProcessPath": {{JsonSerializer.Serialize(resolvedPath)}}
            }
            """) ?? throw new InvalidOperationException("无法建立更新恢复测试状态");
        var provider = new OwnershipTestUpdateProvider(executablePath)
        {
            CurrentVersion = "2.0.0"
        };

        var result = await provider.RecoverAsync(
            new AppSettings(),
            pending,
            CancellationToken.None);

        Assert.Equal(ToolUpdateRecoveryKind.Completed, result.Kind,
            "更新恢复应正常完成");
        Assert.True(updateProcess.HasExited, "更新恢复应关闭自更新程序进程");
    }
    finally
    {
        await EnsureCloseWindowProcessesExitedAsync(executablePath);
    }
}
static async Task ToolUpdateRecoveryWaitsForStableFingerprintAsync()
{
    using var area = TestArea.Create();
    var executablePath = CreateCloseWindowExecutable(area);
    var stagedFingerprintPath = area.File("staged-write.bin");
    await File.WriteAllTextAsync(stagedFingerprintPath, "stage-0");
    using var updateProcess = StartCloseWindowProcess(executablePath);
    try
    {
        await WaitForMainWindowAsync(updateProcess);
        var updateStartedAt = new DateTimeOffset(updateProcess.StartTime.ToUniversalTime());
        var resolvedPath = Path.GetFullPath(updateProcess.MainModule?.FileName
            ?? throw new InvalidOperationException("无法取得稳定指纹测试进程实际路径"));
        var observations = new List<(bool ProcessExited, string StagedContent)>();
        var stagedWrites = 0;
        var provider = new OwnershipTestUpdateProvider(executablePath)
        {
            CurrentVersion = "2.0.0",
            AdditionalFingerprintPath = stagedFingerprintPath,
            ReadVersionCallback = readCount =>
            {
                observations.Add((updateProcess.HasExited, File.ReadAllText(stagedFingerprintPath)));
                if (readCount <= 2)
                {
                    File.WriteAllText(stagedFingerprintPath, $"stage-{readCount}");
                    stagedWrites++;
                }
            }
        };
        var pending = new ToolUpdatePendingState
        {
            ToolId = ToolId.BetterGi,
            TargetVersion = "2.0.0",
            BeforeFingerprint = FakeToolUpdateProvider.Fingerprint("1.0.0"),
            StartedAt = updateStartedAt,
            ProcessId = updateProcess.Id,
            ProcessPath = resolvedPath,
            InstallationPath = Path.GetFullPath(executablePath)
        };

        var result = await provider.RecoverAsync(
            new AppSettings(),
            pending,
            CancellationToken.None);

        var observationsBeforeClose = observations
            .Where(observation => !observation.ProcessExited)
            .Select(observation => observation.StagedContent)
            .ToArray();
        Assert.True(observationsBeforeClose.Length >= 4,
            $"恢复关闭进程前应至少完成四次完整指纹观察；实际：{observationsBeforeClose.Length}");
        Assert.Equal(observationsBeforeClose[^2], observationsBeforeClose[^1],
            "恢复关闭进程前必须观察到连续两个相同的完整指纹");
        Assert.Equal("stage-2", observationsBeforeClose[^1],
            "连续稳定的完整指纹必须包含全部分阶段写入");
        Assert.Equal(2, stagedWrites, "恢复关闭进程前必须完成全部分阶段写入");
        Assert.Equal(ToolUpdateRecoveryKind.Completed, result.Kind,
            $"完整指纹稳定后的更新恢复应完成：{result.Message}");
        Assert.True(updateProcess.HasExited, "完整指纹稳定后应正常关闭事务拥有的进程");

        var freshFingerprint = await provider.CaptureFingerprintForTestAsync();
        Assert.True(result.Fingerprint is not null, "完成的更新恢复必须返回最终指纹");
        Assert.Equal(freshFingerprint.Version, result.Fingerprint!.Version,
            "恢复返回版本必须与关闭后的新鲜指纹一致");
        Assert.Equal(freshFingerprint.Sha256, result.Fingerprint.Sha256,
            "恢复返回哈希必须与关闭后的新鲜指纹一致");
        Assert.Equal(freshFingerprint.TotalLength, result.Fingerprint.TotalLength,
            "恢复返回长度必须与关闭后的新鲜指纹一致");
    }
    finally
    {
        await EnsureCloseWindowProcessesExitedAsync(executablePath);
    }
}

static async Task ToolUpdateRecoveryWaitsForUpdaterExitAfterQuietFingerprintAsync()
{
    using var area = TestArea.Create();
    var executablePath = CreateCloseWindowExecutable(area);
    var stagedFingerprintPath = area.File("delayed-stage.bin");
    await File.WriteAllTextAsync(stagedFingerprintPath, "stage-0");
    using var updateProcess = StartCloseWindowProcess(executablePath);
    try
    {
        await WaitForMainWindowAsync(updateProcess);
        var updateStartedAt = new DateTimeOffset(updateProcess.StartTime.ToUniversalTime());
        var resolvedPath = Path.GetFullPath(updateProcess.MainModule?.FileName
            ?? throw new InvalidOperationException("无法取得延迟写入测试进程实际路径"));
        var provider = new OwnershipTestUpdateProvider(executablePath)
        {
            CurrentVersion = "2.0.0",
            AdditionalFingerprintPath = stagedFingerprintPath,
            WaitForUpdateProcessExit = true
        };
        var pending = new ToolUpdatePendingState
        {
            ToolId = ToolId.BetterGi,
            TargetVersion = "2.0.0",
            BeforeFingerprint = FakeToolUpdateProvider.Fingerprint("1.0.0"),
            StartedAt = updateStartedAt,
            ProcessId = updateProcess.Id,
            ProcessPath = resolvedPath,
            InstallationPath = Path.GetFullPath(executablePath)
        };
        var finishUpdate = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            var closedBeforeFinalWrite = updateProcess.HasExited;
            await File.WriteAllTextAsync(stagedFingerprintPath, "stage-1");
            if (!updateProcess.HasExited)
            {
                _ = updateProcess.CloseMainWindow();
                await updateProcess.WaitForExitAsync();
            }

            return closedBeforeFinalWrite;
        });

        var result = await provider.RecoverAsync(new AppSettings(), pending, CancellationToken.None);
        var closedBeforeFinalWrite = await finishUpdate;
        var finalFingerprint = await provider.CaptureFingerprintForTestAsync();

        Assert.False(closedBeforeFinalWrite,
            "短暂静默期间不得请求关闭仍会继续写入的自更新进程");
        Assert.Equal(ToolUpdateRecoveryKind.Completed, result.Kind,
            $"自更新进程完成后的恢复应成功：{result.Message}");
        Assert.True(result.Fingerprint is not null, "恢复成功必须返回最终指纹");
        Assert.Equal(finalFingerprint.Sha256, result.Fingerprint!.Sha256,
            "恢复结果必须包含自更新进程退出前的最后一次写入");
    }
    finally
    {
        await EnsureCloseWindowProcessesExitedAsync(executablePath);
    }
}

static async Task MaaEndRecoveryRetriesIncompleteInterfaceWhileUpdaterRunsAsync()
{
    using var area = TestArea.Create();
    var executablePath = CreateCloseWindowExecutable(area);
    var interfacePath = area.File("interface.json");
    await File.WriteAllTextAsync(interfacePath, "{}");
    using var updateProcess = StartCloseWindowProcess(executablePath);
    try
    {
        await WaitForMainWindowAsync(updateProcess);
        var updateStartedAt = new DateTimeOffset(updateProcess.StartTime.ToUniversalTime());
        var resolvedPath = Path.GetFullPath(updateProcess.MainModule?.FileName
            ?? throw new InvalidOperationException("无法取得 MaaEnd 恢复测试进程实际路径"));
        var pending = new ToolUpdatePendingState
        {
            ToolId = ToolId.MaaEnd,
            TargetVersion = "v2.0.0",
            BeforeFingerprint = FakeToolUpdateProvider.Fingerprint("v1.0.0"),
            StartedAt = updateStartedAt,
            ProcessId = updateProcess.Id,
            ProcessPath = resolvedPath,
            InstallationPath = Path.GetFullPath(executablePath)
        };
        var finishUpdate = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            await File.WriteAllTextAsync(interfacePath, "{\"version\":\"v2.0.0\"}");
            _ = updateProcess.CloseMainWindow();
            await updateProcess.WaitForExitAsync();
        });
        ToolUpdateRecoveryResult? result = null;
        Exception? recoveryError = null;
        try
        {
            result = await new MaaEndUpdateProvider(updateTimeout: TimeSpan.FromSeconds(10))
                .RecoverAsync(
                    new AppSettings { MaaEndPath = executablePath },
                    pending,
                    CancellationToken.None);
        }
        catch (Exception exception)
        {
            recoveryError = exception;
        }

        await finishUpdate;
        Assert.True(recoveryError is null,
            $"自更新进程仍在运行时，暂缺版本的 interface.json 应作为瞬态写入重试：{recoveryError?.Message}");
        Assert.Equal(ToolUpdateRecoveryKind.Completed, result!.Kind,
            $"interface.json 写入完成后的恢复应成功：{result.Message}");
    }
    finally
    {
        await EnsureCloseWindowProcessesExitedAsync(executablePath);
    }
}

static async Task ToolUpdateRecoveryWaitsForWorkerExitAfterQuietFingerprintAsync()
{
    using var area = TestArea.Create();
    var executablePath = CreateCloseWindowExecutable(area);
    var workerPath = area.File("MAA.Updater.exe");
    File.Copy(executablePath, workerPath);
    var stagedFingerprintPath = area.File("worker-stage.bin");
    await File.WriteAllTextAsync(stagedFingerprintPath, "stage-0");

    using var updateProcess = StartCloseWindowProcess(executablePath);
    await WaitForMainWindowAsync(updateProcess);
    var updateStartedAt = new DateTimeOffset(updateProcess.StartTime.ToUniversalTime());
    var resolvedPath = Path.GetFullPath(updateProcess.MainModule?.FileName
        ?? throw new InvalidOperationException("无法取得外部更新器测试进程实际路径"));
    _ = updateProcess.CloseMainWindow();
    await updateProcess.WaitForExitAsync();

    using var workerProcess = StartCloseWindowProcess(workerPath);
    try
    {
        await WaitForMainWindowAsync(workerProcess);
        var provider = new OwnershipTestUpdateProvider(executablePath)
        {
            CurrentVersion = "2.0.0",
            AdditionalFingerprintPath = stagedFingerprintPath,
            UpdateWorkerProcessPath = workerPath
        };
        var pending = new ToolUpdatePendingState
        {
            ToolId = ToolId.BetterGi,
            TargetVersion = "2.0.0",
            BeforeFingerprint = FakeToolUpdateProvider.Fingerprint("1.0.0"),
            StartedAt = updateStartedAt,
            ProcessId = updateProcess.Id,
            ProcessPath = resolvedPath,
            InstallationPath = Path.GetFullPath(executablePath)
        };
        var finishUpdate = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await File.WriteAllTextAsync(stagedFingerprintPath, "stage-1");
            _ = workerProcess.CloseMainWindow();
            await workerProcess.WaitForExitAsync();
        });

        var result = await provider.RecoverAsync(new AppSettings(), pending, CancellationToken.None);
        await finishUpdate;
        var finalFingerprint = await provider.CaptureFingerprintForTestAsync();

        Assert.Equal(ToolUpdateRecoveryKind.Completed, result.Kind,
            $"外部更新器完成后的恢复应成功：{result.Message}");
        Assert.True(result.Fingerprint is not null, "恢复成功必须返回最终指纹");
        Assert.Equal(finalFingerprint.Sha256, result.Fingerprint!.Sha256,
            "恢复结果必须包含外部更新器退出前的最后一次写入");
    }
    finally
    {
        await EnsureCloseWindowProcessesExitedAsync(workerPath);
        await EnsureCloseWindowProcessesExitedAsync(executablePath);
    }
}

static async Task ToolUpdateClosesProcessRestartedByWorkerAsync()
{
    using var area = TestArea.Create();
    var executablePath = CreateCloseWindowExecutable(area);
    var workerPath = area.File("MAA.Updater.exe");
    File.Copy(executablePath, workerPath);
    var provider = new OwnershipTestUpdateProvider(executablePath)
    {
        CurrentVersion = "2.0.0",
        WaitForUpdateProcessExit = true,
        UpdateWorkerProcessPath = workerPath
    };
    Process? updateProcess = null;
    Process? workerProcess = null;
    Process? restartedProcess = null;
    var context = new ToolUpdateExecutionContext(
        async (processId, _, _) =>
        {
            updateProcess = Process.GetProcessById(processId);
            await WaitForMainWindowAsync(updateProcess);

            workerProcess = StartCloseWindowProcess(workerPath);
            await WaitForMainWindowAsync(workerProcess);

            _ = updateProcess.CloseMainWindow();
            await updateProcess.WaitForExitAsync();

            restartedProcess = StartCloseWindowProcess(executablePath);
            await WaitForMainWindowAsync(restartedProcess);

            _ = workerProcess.CloseMainWindow();
            await workerProcess.WaitForExitAsync();
        },
        (_, _) => Task.CompletedTask);

    try
    {
        var result = await provider.UpdateAsync(
            new AppSettings(),
            new ToolUpdateCheckResult(
                ToolId.BetterGi,
                FakeToolUpdateProvider.Fingerprint("1.0.0"),
                "2.0.0",
                true),
            context,
            CancellationToken.None);

        Assert.True(result.Succeeded, $"更新应完成：{result.Message}");
        Assert.True(restartedProcess is not null && restartedProcess.HasExited,
            "自更新完成后由更新器拉起的新版本进程必须被正常关闭");
    }
    finally
    {
        updateProcess?.Dispose();
        workerProcess?.Dispose();
        restartedProcess?.Dispose();
        await EnsureCloseWindowProcessesExitedAsync(workerPath);
        await EnsureCloseWindowProcessesExitedAsync(executablePath);
    }
}

static async Task ToolUpdateRecoveryClosesProcessRestartedByWorkerAsync()
{
    using var area = TestArea.Create();
    var executablePath = CreateCloseWindowExecutable(area);
    var workerPath = area.File("MAA.Updater.exe");
    File.Copy(executablePath, workerPath);

    using var updateProcess = StartCloseWindowProcess(executablePath);
    await WaitForMainWindowAsync(updateProcess);
    var updateStartedAt = new DateTimeOffset(updateProcess.StartTime.ToUniversalTime());
    var resolvedPath = Path.GetFullPath(updateProcess.MainModule?.FileName
        ?? throw new InvalidOperationException("无法取得外部更新器测试进程实际路径"));

    using var workerProcess = StartCloseWindowProcess(workerPath);
    await WaitForMainWindowAsync(workerProcess);

    _ = updateProcess.CloseMainWindow();
    await updateProcess.WaitForExitAsync();

    using var restartedProcess = StartCloseWindowProcess(executablePath);
    await WaitForMainWindowAsync(restartedProcess);
    _ = workerProcess.CloseMainWindow();
    await workerProcess.WaitForExitAsync();

    var provider = new OwnershipTestUpdateProvider(executablePath)
    {
        CurrentVersion = "2.0.0",
        WaitForUpdateProcessExit = true,
        UpdateWorkerProcessPath = workerPath
    };
    var pending = new ToolUpdatePendingState
    {
        ToolId = ToolId.BetterGi,
        TargetVersion = "2.0.0",
        BeforeFingerprint = FakeToolUpdateProvider.Fingerprint("1.0.0"),
        StartedAt = updateStartedAt,
        ProcessId = updateProcess.Id,
        ProcessPath = resolvedPath,
        InstallationPath = Path.GetFullPath(executablePath)
    };

    try
    {
        var result = await provider.RecoverAsync(new AppSettings(), pending, CancellationToken.None);

        Assert.Equal(ToolUpdateRecoveryKind.Completed, result.Kind,
            $"外部更新器拉起新进程后的恢复应完成：{result.Message}");
        Assert.True(restartedProcess.HasExited,
            "更新恢复完成后由更新器拉起的新版本进程必须被正常关闭");
    }
    finally
    {
        await EnsureCloseWindowProcessesExitedAsync(workerPath);
        await EnsureCloseWindowProcessesExitedAsync(executablePath);
    }
}

static async Task BetterGiRecoveryRejectsDifferentInstallationAsync()
{
    using var installationA = TestArea.Create();
    using var installationB = TestArea.Create();
    var executableA = CreateCloseWindowExecutable(installationA);
    var executableB = CreateCloseWindowExecutable(installationB);
    using var userProcessB = StartCloseWindowProcess(executableB);
    try
    {
        await WaitForMainWindowAsync(userProcessB);
        await using var lockedExecutableB = new FileStream(
            executableB,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        var pending = JsonSerializer.Deserialize<ToolUpdatePendingState>($$"""
            {
              "ToolId": 0,
              "TargetVersion": "2.0.0",
              "BeforeFingerprint": {
                "Version": "1.0.0",
                "Sha256": "HASH-1.0.0",
                "TotalLength": 5,
                "CapturedAt": "2026-08-30T00:00:00+00:00"
              },
              "StartedAt": "2026-08-30T00:00:00+00:00",
              "InstallationPath": {{JsonSerializer.Serialize(Path.GetFullPath(executableA))}}
            }
            """) ?? throw new InvalidOperationException("无法建立 BetterGI 跨安装恢复状态");

        var result = await new BetterGiUpdateProvider().RecoverAsync(
            new AppSettings { BetterGiPath = executableB },
            pending,
            CancellationToken.None);

        Assert.Equal(ToolUpdateRecoveryKind.Failed, result.Kind,
            "BetterGI 跨安装恢复必须失败");
        Assert.True(result.Message.Contains("程序路径在未完成更新后发生变化", StringComparison.Ordinal),
            $"BetterGI 应在检查安装 B 前拒绝跨安装恢复；实际：{result.Message}");
        Assert.False(userProcessB.HasExited,
            "BetterGI 跨安装恢复不得关闭安装 B 中的用户进程");
    }
    finally
    {
        await EnsureCloseWindowProcessesExitedAsync(executableB);
    }
}

static async Task MaaEndRecoveryRejectsDifferentInstallationAsync()
{
    using var installationA = TestArea.Create();
    using var installationB = TestArea.Create();
    var executableA = CreateCloseWindowExecutable(installationA);
    var executableB = CreateCloseWindowExecutable(installationB);
    await File.WriteAllTextAsync(installationA.File("interface.json"), """
        { "version": "1.0.0", "github": "https://github.com/MaaEnd/MaaEnd" }
        """);
    await File.WriteAllTextAsync(installationB.File("interface.json"), """
        { "version": "2.0.0", "github": "https://github.com/MaaEnd/MaaEnd" }
        """);
    using var transactionProcessA = StartCloseWindowProcess(executableA);
    using var userProcessB = StartCloseWindowProcess(executableB);
    try
    {
        await WaitForMainWindowAsync(transactionProcessA);
        await WaitForMainWindowAsync(userProcessB);
        var transactionStartedAt = new DateTimeOffset(transactionProcessA.StartTime.ToUniversalTime());
        var transactionProcessPath = Path.GetFullPath(transactionProcessA.MainModule?.FileName
            ?? throw new InvalidOperationException("无法取得 MaaEnd 事务测试进程实际路径"));
        await using var lockedInterfaceB = new FileStream(
            installationB.File("interface.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        var pending = JsonSerializer.Deserialize<ToolUpdatePendingState>($$"""
            {
              "ToolId": 2,
              "TargetVersion": "2.0.0",
              "BeforeFingerprint": {
                "Version": "1.0.0",
                "Sha256": "HASH-1.0.0",
                "TotalLength": 5,
                "CapturedAt": "2026-08-30T00:00:00+00:00"
              },
              "StartedAt": "{{transactionStartedAt:O}}",
              "ProcessId": {{transactionProcessA.Id}},
              "ProcessPath": {{JsonSerializer.Serialize(transactionProcessPath)}},
              "ProcessStartedAt": "{{transactionStartedAt:O}}",
              "InstallationPath": {{JsonSerializer.Serialize(Path.GetFullPath(executableA))}}
            }
            """) ?? throw new InvalidOperationException("无法建立 MaaEnd 跨安装恢复状态");

        var result = await new MaaEndUpdateProvider().RecoverAsync(
            new AppSettings { MaaEndPath = executableB },
            pending,
            CancellationToken.None);

        Assert.Equal(ToolUpdateRecoveryKind.Failed, result.Kind,
            "MaaEnd 跨安装恢复必须失败");
        Assert.True(result.Message.Contains("程序路径在未完成更新后发生变化", StringComparison.Ordinal),
            $"MaaEnd 应在检查安装 B 前拒绝跨安装恢复；实际：{result.Message}");
        Assert.False(userProcessB.HasExited,
            "MaaEnd 跨安装恢复不得关闭安装 B 中的用户进程");
    }
    finally
    {
        await EnsureCloseWindowProcessesExitedAsync(executableA);
        await EnsureCloseWindowProcessesExitedAsync(executableB);
    }
}

static async Task NormalizedInstallationPathDoesNotBlockRecoveryAsync()
{
    using var installation = TestArea.Create();
    var executablePath = CreateCloseWindowExecutable(installation);
    await using var lockedExecutable = new FileStream(
        executablePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.None);
    var equivalentPath = Path.Combine(
        Path.GetDirectoryName(executablePath)!.ToUpperInvariant(),
        ".",
        Path.GetFileName(executablePath).ToUpperInvariant());
    var pending = JsonSerializer.Deserialize<ToolUpdatePendingState>($$"""
        {
          "ToolId": 0,
          "TargetVersion": "2.0.0",
          "BeforeFingerprint": {
            "Version": "1.0.0",
            "Sha256": "HASH-1.0.0",
            "TotalLength": 5,
            "CapturedAt": "2026-08-30T00:00:00+00:00"
          },
          "StartedAt": "2026-08-30T00:00:00+00:00",
          "InstallationPath": {{JsonSerializer.Serialize(equivalentPath)}}
        }
        """) ?? throw new InvalidOperationException("无法建立规范化路径恢复状态");

    var result = await new BetterGiUpdateProvider().RecoverAsync(
        new AppSettings { BetterGiPath = executablePath },
        pending,
        CancellationToken.None);

    Assert.True(result.Message.Contains("文件无法检查", StringComparison.Ordinal),
        $"同一安装的大小写和点路径应通过身份校验并继续检查：{result.Message}");
    Assert.False(result.Message.Contains("程序路径在未完成更新后发生变化", StringComparison.Ordinal),
        "规范化等价路径不得被误判为跨安装");
}

static async Task LegacyUpdateStateWithoutInstallationFailsClosedAsync()
{
    using var installation = TestArea.Create();
    var executablePath = CreateCloseWindowExecutable(installation);
    await using var lockedExecutable = new FileStream(
        executablePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.None);
    var result = await new BetterGiUpdateProvider().RecoverAsync(
        new AppSettings { BetterGiPath = executablePath },
        new ToolUpdatePendingState
        {
            ToolId = ToolId.BetterGi,
            TargetVersion = "2.0.0",
            BeforeFingerprint = FakeToolUpdateProvider.Fingerprint("1.0.0"),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        },
        CancellationToken.None);

    Assert.Equal(ToolUpdateRecoveryKind.Failed, result.Kind,
        "旧 BetterGI state 缺少安装身份时必须 fail closed");
    Assert.True(result.Message.Contains("缺少安装路径", StringComparison.Ordinal),
        $"旧 state 应在检查当前安装前提示恢复原路径：{result.Message}");
}

static async Task ToolUpdatePendingPersistsInstallationPathAsync()
{
    using var area = TestArea.Create();
    var installationPath = Path.Combine(area.Root, "工具", ".", "BetterGI.exe");
    var releaseUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi, updateAvailable: true)
    {
        CheckHandler = _ => Task.FromResult(
            FakeToolUpdateProvider.Check(ToolId.BetterGi, updateAvailable: true) with
            {
                InstallationPath = Path.GetFullPath(installationPath)
            }),
        UpdateHandler = async (_, _) =>
        {
            await releaseUpdate.Task;
            return ToolUpdateExecutionResult.Failure("保留测试 pending", recoveryRequired: true);
        }
    };
    var store = new ToolUpdateStateStore(area.File("state"));
    var coordinator = new ToolUpdateCoordinator([provider], store);
    var prepare = coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings { UpdateToolsBeforeLaunch = true });
    await provider.UpdateStarted.Task;
    try
    {
        var state = await store.LoadAsync();
        Assert.True(state.PendingUpdates.TryGetValue(ToolId.BetterGi, out var pending),
            "更新启动前应保存 pending state");
        Assert.Equal(Path.GetFullPath(installationPath), pending!.InstallationPath,
            "pending state 应保存检查时的规范化安装身份");
    }
    finally
    {
        releaseUpdate.TrySetResult();
        _ = await prepare;
    }
}

static string CreateCloseWindowExecutable(TestArea area)
{
    var sourceExecutable = Environment.ProcessPath
        ?? throw new InvalidOperationException("无法取得测试进程路径");
    var sourceRoot = Path.GetDirectoryName(sourceExecutable)
        ?? throw new InvalidOperationException("无法取得测试进程目录");
    var helperPath = area.File(Path.GetFileName(sourceExecutable));
    File.Copy(sourceExecutable, helperPath);
    foreach (var fileName in new[]
             {
                 "GachaOps.Core.Tests.dll",
                 "GachaOps.Core.Tests.deps.json",
                 "GachaOps.Core.Tests.runtimeconfig.json",
                 "GachaOps.Core.dll"
             })
    {
        File.Copy(Path.Combine(sourceRoot, fileName), area.File(fileName));
    }

    return helperPath;
}

static Process StartCloseWindowProcess(string executablePath)
{
    var startInfo = new ProcessStartInfo(executablePath)
    {
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("--bf01-close-window-helper");
    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("无法启动进程所有权测试夹具");
}

static async Task WaitForMainWindowAsync(Process process)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!timeout.IsCancellationRequested)
    {
        process.Refresh();
        if (process.HasExited)
        {
            throw new InvalidOperationException("进程所有权测试夹具在窗口创建前退出");
        }

        if (process.MainWindowHandle != nint.Zero)
        {
            return;
        }

        await Task.Delay(25, timeout.Token);
    }

    throw new TimeoutException("进程所有权测试夹具未在限定时间内创建窗口");
}

static async Task EnsureCloseWindowProcessesExitedAsync(string executablePath)
{
    var processName = Path.GetFileNameWithoutExtension(executablePath);
    foreach (var process in Process.GetProcessesByName(processName))
    {
        using (process)
        {
            try
            {
                if (process.HasExited
                    || !string.Equals(
                        Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                        Path.GetFullPath(executablePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                               or System.ComponentModel.Win32Exception)
            {
            }
        }
    }
}

static Task MaaBetaUpdateChannelIsAllowedAsync()
{
    using var area = TestArea.Create();
    var settings = CreateTestMaaProviderSettings(area);
    var root = Path.GetDirectoryName(settings.MaaPath)!;
    File.WriteAllText(Path.Combine(root, "config", "gui.new.json"), """
        {
          "Update": {
            "CheckOnStartup": true,
            "AutoDownloadUpdatePackage": true,
            "AutoInstallUpdatePackage": true,
            "VersionType": "Beta"
          }
        }
        """);
    var provider = CreateTestMaaUpdateProvider(
        new FakeMaaResourceUpdateModule(),
        new FakeMaaProgramUpdateOperations());

    var validation = provider.ValidateUpdate(settings);

    Assert.True(validation.IsValid,
        $"用户选择 MAA 测试版通道时不应被 GachaOps 阻止：{string.Join("；", validation.Issues)}");
    return Task.CompletedTask;
}

static Task MaaEndBetaUpdateChannelIsAllowedAsync()
{
    using var area = TestArea.Create();
    var root = Path.Combine(area.Root, "MaaEnd provider 中文");
    Directory.CreateDirectory(Path.Combine(root, "config"));
    var executablePath = Path.Combine(root, "MaaEnd.exe");
    File.WriteAllBytes(executablePath, [0x4D, 0x5A, 0x00, 0x00]);
    File.WriteAllText(Path.Combine(root, "interface.json"), """
        {
          "version": "v2.26.0-beta.6",
          "github": "https://github.com/MaaEnd/MaaEnd"
        }
        """);
    File.WriteAllText(Path.Combine(root, "config", "mxu-MaaEnd.json"), """
        {
          "settings": {
            "autoRunOnLaunch": false,
            "mirrorChyan": {
              "channel": "beta"
            }
          }
        }
        """);
    var provider = new MaaEndUpdateProvider(new StaticGitHubReleaseClient());

    var validation = provider.ValidateUpdate(new AppSettings { MaaEndPath = executablePath });

    Assert.True(validation.IsValid,
        $"用户选择 MaaEnd 测试版通道时不应被 GachaOps 阻止：{string.Join("；", validation.Issues)}");
    return Task.CompletedTask;
}

static async Task DisabledMaaSkipsResourceChecksAsync()
{
    using var area = TestArea.Create();
    var resource = new FakeMaaResourceUpdateModule();
    var maa = CreateTestMaaUpdateProvider(resource, new FakeMaaProgramUpdateOperations());
    var betterGi = new FakeToolUpdateProvider(ToolId.BetterGi);
    var coordinator = new ToolUpdateCoordinator([betterGi, maa], new ToolUpdateStateStore(area.Root));

    var result = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi), new PreflightAdapter(ToolId.Maa)],
        [
            new WorkflowTaskSetting { ToolId = ToolId.BetterGi, IsEnabled = true, Channel = 1 },
            new WorkflowTaskSetting { ToolId = ToolId.Maa, IsEnabled = false, Channel = 1 }
        ],
        new AppSettings { UpdateToolsBeforeLaunch = true });

    Assert.True(result.Succeeded, "仅启用 BetterGI 的准备流程应成功");
    Assert.Equal(0, resource.CheckCount, "MAA 未启用时不应检查资源版本");
    Assert.Equal(0, resource.ScopeCaptureCount, "MAA 未启用时不应读取资源指纹");
}

static async Task MaaResourcePathsAreNormalizedAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area, "非D盘 MAA 中文 空格");
    var pathWithDotSegment = Path.Combine(fixture.InstallationRoot, ".", "MAA.exe");

    Assert.True(fixture.Module.Validate(pathWithDotSegment).IsValid,
        "规范化后的空格和中文绝对路径应通过校验");
    Assert.True((await fixture.Module.CaptureLocalFingerprintAsync(
        pathWithDotSegment,
        CancellationToken.None)).Length == 64,
        "MAA resource 应能生成完整 SHA-256 指纹");
    Assert.False(fixture.Module.Validate("MAA.exe").IsValid, "相对路径必须被拒绝");

    var linkRoot = Path.Combine(area.Root, "maa-link");
    try
    {
        Directory.CreateSymbolicLink(linkRoot, fixture.InstallationRoot);
        Assert.False(fixture.Module.Validate(Path.Combine(linkRoot, "MAA.exe")).IsValid,
            "符号链接安装路径必须被拒绝");
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
    {
        // Windows 未允许创建测试符号链接时，归档符号链接拒绝仍由独立测试覆盖。
    }
}

static async Task MaaResourceCheckUsesLightweightFingerprintAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area);
    var unrelatedResource = Path.Combine(fixture.ResourceRoot, "stages.json");
    await using var exclusiveLock = new FileStream(
        unrelatedResource,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.None);

    var fingerprint = await fixture.Module.CaptureLocalFingerprintAsync(
        fixture.MaaPath,
        CancellationToken.None);
    var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);

    Assert.Equal(64, fingerprint.Length, "轻量检查仍应返回 SHA-256 指纹");
    Assert.True(plan.UpdateAvailable, "锁住无关资源文件时仍应完成版本检查");
}

static async Task MaaResourceOnlyUpdateIsDetectedAndAppliedAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area);
    var maaBefore = await File.ReadAllBytesAsync(fixture.MaaPath);

    var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    var result = await fixture.Module.UpdateAsync(fixture.MaaPath, plan, CancellationToken.None);

    Assert.True(plan.UpdateAvailable, "本地旧资源应报告 UpdateAvailable");
    Assert.True(result.Succeeded, "仅资源更新应成功");
    Assert.Equal(fixture.TargetVersion, ReadResourceVersionForTest(fixture.ResourceRoot),
        "资源版本未更新到目标版本");
    Assert.SequenceEqual(maaBefore, await File.ReadAllBytesAsync(fixture.MaaPath),
        "资源更新不得改写或启动 MAA.exe");
    Assert.Equal(1, fixture.Handler.DownloadCount, "仅资源更新应下载一次固定归档");
}

static async Task MaaResourceOnlyUpdateSkipsProgramUpdaterAsync()
{
    using var area = TestArea.Create();
    var resource = new FakeMaaResourceUpdateModule
    {
        CheckPlans = new Queue<MaaResourceUpdatePlan>(
        [
            new MaaResourceUpdatePlan(
                TestCommit('a'),
                OldResourceVersion,
                NewResourceVersion,
                true)
        ])
    };
    var program = new FakeMaaProgramUpdateOperations();
    var provider = CreateTestMaaUpdateProvider(resource, program);
    var coordinator = new ToolUpdateCoordinator(
        [provider],
        new ToolUpdateStateStore(area.File("resource-only-state")));
    var activities = new ConcurrentQueue<ToolUpdateActivity>();
    coordinator.UpdateActivityChanged += activities.Enqueue;
    var settings = CreateTestMaaProviderSettings(area);
    settings.UpdateToolsBeforeLaunch = true;

    var result = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.Maa)],
        Workflow((ToolId.Maa, 1)),
        settings);

    Assert.True(result.Succeeded, "仅资源更新的准备流程应成功");
    Assert.Equal(0, program.UpdateCount, "仅资源落后时不得调用 MAA.exe 程序更新入口");
    Assert.Equal(1, resource.AppliedPlans.Count, "仅资源落后时应执行一次资源事务");
    Assert.True(activities.Any(activity =>
            activity.Phase == ToolUpdateActivityPhase.Updating
            && activity.Items.SequenceEqual(["MAA 官方资源"])),
        "仅资源落后时应明确显示 MAA 官方资源正在更新");
    Assert.SequenceEqual(["MAA 官方资源"], result.UpdatedItems,
        "仅资源更新完成后应返回明确的完成项目");
}

static async Task MaaProgramAndResourceUpdatesAreSequencedAsync()
{
    using var area = TestArea.Create();
    var order = new List<string>();
    var resource = new FakeMaaResourceUpdateModule(order)
    {
        CheckPlans = new Queue<MaaResourceUpdatePlan>(
        [
            new MaaResourceUpdatePlan(TestCommit('a'), OldResourceVersion, NewResourceVersion, true),
            new MaaResourceUpdatePlan(TestCommit('b'), OldResourceVersion, NewerResourceVersion, true)
        ])
    };
    var program = new FakeMaaProgramUpdateOperations(order)
    {
        ProgramCheck = new ToolUpdateCheckResult(
            ToolId.Maa,
            FakeToolUpdateProvider.Fingerprint("1.0.0"),
            "2.0.0",
            true)
    };
    var provider = CreateTestMaaUpdateProvider(resource, program);
    resource.ProgramUpdated = () => program.ProgramUpdated;
    var settings = CreateTestMaaProviderSettings(area);
    settings.UpdateToolsBeforeLaunch = true;
    var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.File("state-root")));
    var activities = new ConcurrentQueue<ToolUpdateActivity>();
    coordinator.UpdateActivityChanged += activities.Enqueue;

    var result = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.Maa)],
        Workflow((ToolId.Maa, 1)),
        settings);

    Assert.True(result.Succeeded, "MAA 程序和资源串联更新应成功");
    Assert.SequenceEqual(
        ["program-check", "resource-check", "program-update", "resource-check", "resource-update"],
        order,
        "MAA 应先更新程序并退出，再重新检查并更新资源");
    Assert.Equal(TestCommit('b'), resource.AppliedPlans.Single().Commit,
        "程序更新后应使用重新检查得到的固定资源 commit");
    var updatingItems = activities
        .Where(activity => activity.Phase == ToolUpdateActivityPhase.Updating)
        .Select(activity => string.Join("、", activity.Items))
        .ToArray();
    var programActivityIndex = Array.IndexOf(updatingItems, "MAA 程序");
    var resourceActivityIndex = Array.IndexOf(updatingItems, "MAA 官方资源");
    Assert.True(programActivityIndex >= 0, "MAA 程序更新阶段应明确显示");
    Assert.True(resourceActivityIndex > programActivityIndex,
        "程序完成后应切换为 MAA 官方资源更新阶段");
    Assert.SequenceEqual(["MAA 程序", "MAA 官方资源"], result.UpdatedItems,
        "完成提示应包含本轮实际完成的程序和资源更新");
}

static async Task MaaResourcePlanPinsExactCommitAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area);
    var checkedPlan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    fixture.AddRemoteRelease(TestCommit('b'), NewerResourceVersion);
    fixture.Handler.BranchCommit = TestCommit('b');

    var result = await fixture.Module.UpdateAsync(
        fixture.MaaPath,
        checkedPlan,
        CancellationToken.None);

    Assert.True(result.Succeeded, "固定 commit 的资源更新应成功");
    Assert.True(fixture.Handler.RequestedUris.Any(uri =>
            uri.Contains($"/zipball/{TestCommit('a')}", StringComparison.Ordinal)),
        "下载必须继续使用检查阶段锁定的 commit");
    Assert.Equal(NewResourceVersion, ReadResourceVersionForTest(fixture.ResourceRoot),
        "不得在更新阶段悄悄切换到后来出现的 commit");
}

static async Task MaaResourcePlanOnlyRejectsRelevantStateChangesAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area);
    var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    var customPath = Path.Combine(fixture.ResourceRoot, "custom", "user.json");
    await File.AppendAllTextAsync(customPath, "external-change");

    var result = await fixture.Module.UpdateAsync(
        fixture.MaaPath,
        plan,
        CancellationToken.None);

    Assert.True(result.Succeeded, "检查后修改包外自定义文件不应使资源计划失效");
    Assert.Equal(1, fixture.Handler.DownloadCount, "自定义文件变化不应阻止正常资源下载");
    Assert.True((await File.ReadAllTextAsync(customPath)).Contains("external-change", StringComparison.Ordinal),
        "更新必须保留检查后发生变化的包外自定义文件");

    using var relevantArea = TestArea.Create();
    using var relevantFixture = MaaResourceFixture.Create(relevantArea);
    var relevantPlan = await relevantFixture.Module.CheckAsync(
        relevantFixture.MaaPath,
        CancellationToken.None);
    await File.AppendAllTextAsync(
        Path.Combine(relevantFixture.ResourceRoot, "version.json"),
        " ");

    var relevantResult = await relevantFixture.Module.UpdateAsync(
        relevantFixture.MaaPath,
        relevantPlan,
        CancellationToken.None);

    Assert.False(relevantResult.Succeeded, "检查后 version.json 变化必须拒绝旧计划");
    Assert.Equal(0, relevantFixture.Handler.DownloadCount, "关键状态变化后不得下载或进入写入事务");
    Assert.Equal(OldResourceVersion, ReadResourceVersionForTest(relevantFixture.ResourceRoot),
        "拒绝旧计划时不得改变资源版本");

    using var secondArea = TestArea.Create();
    MaaResourceFixture? downloadingFixture = null;
    downloadingFixture = MaaResourceFixture.Create(secondArea, faultInjector: point =>
    {
        if (point == MaaResourceUpdateFaultPoint.AfterArchiveValidated)
        {
            File.AppendAllText(
                Path.Combine(downloadingFixture!.ResourceRoot, "version.json"),
                " ");
        }
    });
    using (downloadingFixture)
    {
        var downloadingPlan = await downloadingFixture.Module.CheckAsync(
            downloadingFixture.MaaPath,
            CancellationToken.None);
        var downloadingResult = await downloadingFixture.Module.UpdateAsync(
            downloadingFixture.MaaPath,
            downloadingPlan,
            CancellationToken.None);

        Assert.False(downloadingResult.Succeeded,
            "下载期间 version.json 变化必须在写入前终止");
        Assert.Equal(OldResourceVersion, ReadResourceVersionForTest(downloadingFixture.ResourceRoot),
            "下载竞争失败时不得改变资源版本");
    }
}

static async Task MaaResourceAtTargetCommitSkipsDownloadAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area);
    var first = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    Assert.True((await fixture.Module.UpdateAsync(
        fixture.MaaPath,
        first,
        CancellationToken.None)).Succeeded,
        "测试前置资源更新失败");
    var downloadsAfterUpdate = fixture.Handler.DownloadCount;

    var second = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);

    Assert.False(second.UpdateAvailable, "已安装并验证为目标 commit 时不应再次更新");
    Assert.Equal(downloadsAfterUpdate, fixture.Handler.DownloadCount,
        "已是目标 commit 的检查不得下载归档");
}

static async Task MaaResourceSecondUpdateOnlyTouchesChangedFilesAsync()
{
    using var area = TestArea.Create();
    MaaResourceFixture? fixture = null;
    var countSecondUpdate = false;
    var secondUpdateWrites = 0;
    var secondSnapshotEntries = -1;
    fixture = MaaResourceFixture.Create(area, faultInjector: point =>
    {
        if (!countSecondUpdate)
        {
            return;
        }

        if (point == MaaResourceUpdateFaultPoint.AfterResourceFileWrite)
        {
            secondUpdateWrites++;
        }
        else if (point == MaaResourceUpdateFaultPoint.AfterBackupPrepared)
        {
            var snapshotPath = Directory.EnumerateFiles(
                    fixture!.AppDataRoot,
                    "snapshot.json",
                    SearchOption.AllDirectories)
                .Single(path => File.ReadAllText(path).Contains(TestCommit('b'), StringComparison.Ordinal));
            using var document = JsonDocument.Parse(File.ReadAllText(snapshotPath));
            secondSnapshotEntries = document.RootElement.GetProperty("Entries").GetArrayLength();
        }
    });
    using (fixture)
    {
        var initialPlan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
        Assert.True((await fixture.Module.UpdateAsync(
            fixture.MaaPath,
            initialPlan,
            CancellationToken.None)).Succeeded, "前置资源更新失败");

        fixture.AddRemoteRelease(TestCommit('b'), NewerResourceVersion);
        fixture.Handler.BranchCommit = TestCommit('b');
        var newerPlan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
        countSecondUpdate = true;

        var result = await fixture.Module.UpdateAsync(
            fixture.MaaPath,
            newerPlan,
            CancellationToken.None);

        Assert.True(result.Succeeded,
            $"仅 version.json 变化的第二次更新应成功：{result.Message}");
        Assert.Equal(1, secondSnapshotEntries, "第二次更新只应备份实际变化的 version.json");
        Assert.Equal(1, secondUpdateWrites, "第二次更新只应写入实际变化的 version.json");
    }
}

static async Task MaaResourceCommitOnlyUpdateWritesNoResourceFilesAsync()
{
    using var area = TestArea.Create();
    MaaResourceFixture? fixture = null;
    var countCommitOnlyUpdate = false;
    var resourceWrites = 0;
    var snapshotEntries = -1;
    fixture = MaaResourceFixture.Create(area, faultInjector: point =>
    {
        if (!countCommitOnlyUpdate)
        {
            return;
        }

        if (point == MaaResourceUpdateFaultPoint.AfterResourceFileWrite)
        {
            resourceWrites++;
        }
        else if (point == MaaResourceUpdateFaultPoint.AfterBackupPrepared)
        {
            var snapshotPath = Directory.EnumerateFiles(
                    fixture!.AppDataRoot,
                    "snapshot.json",
                    SearchOption.AllDirectories)
                .Single(path => File.ReadAllText(path).Contains(TestCommit('b'), StringComparison.Ordinal));
            using var document = JsonDocument.Parse(File.ReadAllText(snapshotPath));
            snapshotEntries = document.RootElement.GetProperty("Entries").GetArrayLength();
        }
    });
    using (fixture)
    {
        var initialPlan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
        Assert.True((await fixture.Module.UpdateAsync(
            fixture.MaaPath,
            initialPlan,
            CancellationToken.None)).Succeeded, "前置资源更新失败");

        fixture.AddRemoteRelease(TestCommit('b'), NewResourceVersion);
        fixture.Handler.BranchCommit = TestCommit('b');
        var commitOnlyPlan = await fixture.Module.CheckAsync(
            fixture.MaaPath,
            CancellationToken.None);
        countCommitOnlyUpdate = true;

        var result = await fixture.Module.UpdateAsync(
            fixture.MaaPath,
            commitOnlyPlan,
            CancellationToken.None);

        Assert.True(result.Succeeded, $"资源内容未变化的 commit 更新应成功：{result.Message}");
        Assert.Equal(0, snapshotEntries, "资源内容未变化时临时快照不应包含资源文件");
        Assert.Equal(0, resourceWrites, "资源内容未变化时不得执行资源文件替换");
        Assert.Equal(NewResourceVersion, ReadResourceVersionForTest(fixture.ResourceRoot),
            "纯 commit 更新不得改变资源版本内容");
    }
}

static async Task MaaResourceNetworkRetriesOnceAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area);
    fixture.Handler.FailBranchRequests = 1;
    fixture.Handler.FailDownloadRequests = 1;

    var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    var result = await fixture.Module.UpdateAsync(fixture.MaaPath, plan, CancellationToken.None);

    Assert.True(result.Succeeded, "一次瞬时网络失败后应重试成功");
    Assert.Equal(2, fixture.Handler.BranchCount, "资源检查只应进行两次尝试");
    Assert.Equal(2, fixture.Handler.DownloadCount, "资源下载只应进行两次尝试");

    using var secondArea = TestArea.Create();
    using var failedFixture = MaaResourceFixture.Create(secondArea);
    failedFixture.Handler.FailBranchRequests = 5;
    var failed = false;
    try
    {
        _ = await failedFixture.Module.CheckAsync(failedFixture.MaaPath, CancellationToken.None);
    }
    catch (HttpRequestException)
    {
        failed = true;
    }

    Assert.True(failed, "连续网络失败应阻止资源检查");
    Assert.Equal(2, failedFixture.Handler.BranchCount, "连续失败也不得超过两次尝试");
}

static async Task MaaResourceDownloadCancellationDoesNotWriteAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area);
    using var cancellation = new CancellationTokenSource();
    fixture.Handler.BlockDownloads = true;
    var plan = new MaaResourceUpdatePlan(
        TestCommit('a'),
        OldResourceVersion,
        NewResourceVersion,
        true);
    var update = fixture.Module.UpdateAsync(fixture.MaaPath, plan, cancellation.Token);
    await fixture.Handler.DownloadStarted.Task;
    cancellation.Cancel();
    var result = await update;

    Assert.True(result.Cancelled, "下载阶段取消应返回取消结果");
    Assert.Equal(OldResourceVersion, ReadResourceVersionForTest(fixture.ResourceRoot),
        "下载取消不得写入 resource");
    Assert.False(Directory.Exists(Path.Combine(fixture.ResourceRoot, "template", "new.txt")),
        "下载取消不得产生包内新增文件");
    Assert.False(Directory.Exists(fixture.AppDataRoot)
                 && Directory.EnumerateFiles(
                     fixture.AppDataRoot,
                     "transaction.json",
                     SearchOption.AllDirectories).Any(),
        "下载取消不得留下待恢复写入事务");
}

static async Task MaaResourceArchiveValidationRejectsUnsafeInputsAsync()
{
    var cases = new (string Name, Func<byte[]> Archive, string TargetVersion)[]
    {
        ("目录穿越", () => CreateMaaResourceArchive(
            NewResourceVersion,
            unsafeEntry: "MaaResource-main/resource/../escape.txt"), NewResourceVersion),
        ("绝对路径", () => CreateMaaResourceArchive(
            NewResourceVersion,
            unsafeEntry: "/absolute.txt"), NewResourceVersion),
        ("符号链接", () => CreateMaaResourceArchive(
            NewResourceVersion,
            unsafeEntry: "MaaResource-main/resource/link",
            unsafeEntryIsSymlink: true), NewResourceVersion),
        ("多个归档根目录", () => CreateMaaResourceArchive(
            NewResourceVersion,
            unsafeEntry: "OtherRoot/resource/extra.json"), NewResourceVersion),
        ("缺少 version.json", () => CreateMaaResourceArchive(
            NewResourceVersion,
            includeVersion: false), NewResourceVersion),
        ("损坏 version.json", () => CreateMaaResourceArchive(
            NewResourceVersion,
            versionJsonOverride: "{"), NewResourceVersion),
        ("目标版本不匹配", () => CreateMaaResourceArchive(NewerResourceVersion), NewResourceVersion)
    };

    foreach (var testCase in cases)
    {
        using var area = TestArea.Create();
        using var fixture = MaaResourceFixture.Create(area);
        fixture.Handler.Archives[TestCommit('a')] = testCase.Archive();
        var result = await fixture.Module.UpdateAsync(
            fixture.MaaPath,
            new MaaResourceUpdatePlan(
                TestCommit('a'),
                OldResourceVersion,
                testCase.TargetVersion,
                true),
            CancellationToken.None);

        Assert.False(result.Succeeded, $"{testCase.Name}归档必须在写入前被拒绝");
        Assert.Equal(OldResourceVersion, ReadResourceVersionForTest(fixture.ResourceRoot),
            $"{testCase.Name}归档不得改变本地版本");
        Assert.False(File.Exists(Path.Combine(fixture.InstallationRoot, "escape.txt")),
            $"{testCase.Name}归档不得写出 resource");
    }
}

static async Task MaaResourceUpdatePreservesCustomFilesAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area);
    var customPath = Path.Combine(fixture.ResourceRoot, "custom", "user.json");
    var existingOfficial = Path.Combine(fixture.ResourceRoot, "template", "existing.txt");
    var customBefore = await File.ReadAllTextAsync(customPath);
    var officialBefore = await File.ReadAllTextAsync(existingOfficial);
    var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);

    var result = await fixture.Module.UpdateAsync(fixture.MaaPath, plan, CancellationToken.None);

    Assert.True(result.Succeeded, "合法资源归档应成功部署");
    Assert.Equal(customBefore, await File.ReadAllTextAsync(customPath),
        "官方包外的自定义额外文件必须保留");
    Assert.False(string.Equals(officialBefore, await File.ReadAllTextAsync(existingOfficial), StringComparison.Ordinal),
        "官方同名文件应被新版本覆盖");
    Assert.True(File.Exists(Path.Combine(fixture.ResourceRoot, "template", "new.txt")),
        "官方包内新增文件应被部署");
    Assert.False(Directory.EnumerateFiles(
            fixture.AppDataRoot,
            "snapshot.json",
            SearchOption.AllDirectories).Any(),
        "成功提交后应清理本次差量回滚快照");
}

static async Task MaaResourcePostWriteVerificationRollsBackAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area);
    var corruptingModule = new MaaResourceUpdateModule(
        fixture.Client,
        fixture.AppDataRoot,
        point =>
        {
            if (point == MaaResourceUpdateFaultPoint.BeforeVerification)
            {
                File.WriteAllText(Path.Combine(fixture.ResourceRoot, "stages.json"), "corrupted");
            }
        });
    var plan = await corruptingModule.CheckAsync(fixture.MaaPath, CancellationToken.None);
    var result = await corruptingModule.UpdateAsync(
        fixture.MaaPath,
        plan,
        CancellationToken.None);

    Assert.False(result.Succeeded, "部署后哈希不匹配必须阻止提交");
    Assert.False(result.RecoveryRequired, "完整回滚后不应保留阻塞事务");
    Assert.Equal(OldResourceVersion, ReadResourceVersionForTest(fixture.ResourceRoot),
        "部署后验证失败应恢复旧 version.json");
    Assert.Equal("old-stages.json", await File.ReadAllTextAsync(
        Path.Combine(fixture.ResourceRoot, "stages.json")),
        "部署后验证失败应恢复所有官方文件");
}

static async Task MaaResourceWriteStageFailuresRollBackAsync()
{
    MaaResourceUpdateFaultPoint[] points =
    {
        MaaResourceUpdateFaultPoint.BeforeResourceWrite,
        MaaResourceUpdateFaultPoint.AfterResourceFileWrite,
        MaaResourceUpdateFaultPoint.BeforeVerification,
        MaaResourceUpdateFaultPoint.BeforeCommit,
        MaaResourceUpdateFaultPoint.AfterCommitAuditWritten
    };
    foreach (var point in points)
    {
        using var area = TestArea.Create();
        using var fixture = MaaResourceFixture.Create(area, faultInjector: current =>
        {
            if (current == point)
            {
                throw new IOException($"fault-{point}");
            }
        });
        var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);

        var result = await fixture.Module.UpdateAsync(fixture.MaaPath, plan, CancellationToken.None);

        Assert.False(result.Succeeded, $"{point} 故障必须阻止工作流");
        Assert.False(result.RecoveryRequired, $"{point} 故障应在本轮完整回滚");
        Assert.Equal(OldResourceVersion, ReadResourceVersionForTest(fixture.ResourceRoot),
            $"{point} 故障后 version.json 未恢复");
        Assert.Equal("old-stages.json", await File.ReadAllTextAsync(
            Path.Combine(fixture.ResourceRoot, "stages.json")),
            $"{point} 故障后官方文件未恢复");
        Assert.False(File.Exists(Path.Combine(fixture.ResourceRoot, "template", "new.txt")),
            $"{point} 故障后包内新增文件未删除");
    }
}

static async Task MaaResourcePreparedCrashRecoversAsync()
{
    foreach (var legacyPhase in new[] { false, true })
    {
        using var area = TestArea.Create();
        using var fixture = MaaResourceFixture.Create(area, faultInjector: point =>
        {
            if (point == MaaResourceUpdateFaultPoint.AfterBackupPrepared)
                throw new MaaResourceSimulatedCrashException("backup crash");
        });
        var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
        var crashed = false;
        try
        {
            await fixture.Module.UpdateAsync(fixture.MaaPath, plan, CancellationToken.None);
        }
        catch (MaaResourceSimulatedCrashException)
        {
            crashed = true;
        }
        Assert.True(crashed, "必须在备份持久化后模拟崩溃");
        var transactionPath = Directory.EnumerateFiles(
            fixture.AppDataRoot, "transaction.json", SearchOption.AllDirectories).Single();
        var transaction = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(transactionPath))!;
        Assert.Equal("Prepared", transaction["Phase"]!.GetValue<string>(), "新事务应直接持久化 Prepared");
        if (legacyPhase)
        {
            transaction["Phase"] = "BackupReady";
            await File.WriteAllTextAsync(transactionPath, transaction.ToJsonString());
        }
        var recovery = await new MaaResourceUpdateModule(fixture.Client, fixture.AppDataRoot)
            .RecoverAsync(fixture.MaaPath, CancellationToken.None);
        Assert.Equal(MaaResourceRecoveryKind.RolledBack, recovery.Kind, "新旧备份阶段均应允许恢复");
        Assert.Equal(OldResourceVersion, ReadResourceVersionForTest(fixture.ResourceRoot), "备份后崩溃不得改写资源");
        Assert.False(File.Exists(transactionPath), "恢复后应清理事务日志");
    }
}

static async Task MaaResourceInstallationStateFingerprintCompatibilityAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area);
    var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    Assert.True((await fixture.Module.UpdateAsync(fixture.MaaPath, plan, CancellationToken.None)).Succeeded,
        "前置更新应成功");
    var statePath = Directory.EnumerateFiles(
        fixture.AppDataRoot, "installation-state.json", SearchOption.AllDirectories).Single();
    var state = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(statePath))!;
    Assert.True(state["ResourceFingerprint"] is null, "新安装状态不应保存未使用的指纹");
    var fingerprint = await fixture.Module.CaptureLocalFingerprintAsync(fixture.MaaPath, CancellationToken.None);
    state["ResourceFingerprint"] = new string('A', 64);
    await File.WriteAllTextAsync(statePath, state.ToJsonString());
    Assert.Equal(fingerprint,
        await fixture.Module.CaptureLocalFingerprintAsync(fixture.MaaPath, CancellationToken.None),
        "旧字段应被兼容读取且不影响即时指纹");
    Assert.False((await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None)).UpdateAvailable,
        "旧安装状态应仍识别为已更新");
    state["ManifestSha256"] = new string('B', 64);
    await File.WriteAllTextAsync(statePath, state.ToJsonString());
    var rejected = false;
    try
    {
        await fixture.Module.CaptureLocalFingerprintAsync(fixture.MaaPath, CancellationToken.None);
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }
    Assert.True(rejected, "删除冗余字段不能削弱清单完整性校验");
}

static async Task MaaResourceCrashRecoveryRollsBackAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area, faultInjector: point =>
    {
        if (point == MaaResourceUpdateFaultPoint.AfterResourceFileWrite)
        {
            throw new MaaResourceSimulatedCrashException("simulated crash");
        }
    });
    var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    var crashed = false;
    try
    {
        _ = await fixture.Module.UpdateAsync(fixture.MaaPath, plan, CancellationToken.None);
    }
    catch (MaaResourceSimulatedCrashException)
    {
        crashed = true;
    }

    Assert.True(crashed, "测试必须在资源写入后模拟进程崩溃");
    var recoveryModule = new MaaResourceUpdateModule(fixture.Client, fixture.AppDataRoot);
    var recovery = await recoveryModule.RecoverAsync(fixture.MaaPath, CancellationToken.None);

    Assert.Equal(MaaResourceRecoveryKind.RolledBack, recovery.Kind,
        "下次运行应根据事务日志完整回滚");
    Assert.Equal(OldResourceVersion, ReadResourceVersionForTest(fixture.ResourceRoot),
        "崩溃恢复后应还原旧资源版本");
    Assert.False(File.Exists(Path.Combine(fixture.ResourceRoot, "template", "new.txt")),
        "崩溃恢复后应删除事务新增文件");
}

static async Task MaaResourceCommittedCrashRecoversAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area, faultInjector: point =>
    {
        if (point == MaaResourceUpdateFaultPoint.AfterCommitStateWritten)
        {
            throw new MaaResourceSimulatedCrashException("simulated committed crash");
        }
    });
    var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    var crashed = false;
    try
    {
        _ = await fixture.Module.UpdateAsync(fixture.MaaPath, plan, CancellationToken.None);
    }
    catch (MaaResourceSimulatedCrashException)
    {
        crashed = true;
    }

    Assert.True(crashed, "测试必须在资源事务持久提交后模拟进程崩溃");
    var recoveryModule = new MaaResourceUpdateModule(fixture.Client, fixture.AppDataRoot);
    var recovery = await recoveryModule.RecoverAsync(fixture.MaaPath, CancellationToken.None);

    Assert.Equal(MaaResourceRecoveryKind.Committed, recovery.Kind,
        "已持久提交的事务应验证并完成收尾，而不是回滚");
    Assert.Equal(NewResourceVersion, ReadResourceVersionForTest(fixture.ResourceRoot),
        "提交后崩溃恢复不得丢失已验证资源");
    Assert.False(Directory.EnumerateFiles(
            fixture.AppDataRoot,
            "transaction.json",
            SearchOption.AllDirectories).Any(),
        "已提交事务恢复后应清理事务日志");
}

static async Task MaaResourceRollbackFailurePreservesEvidenceAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area, faultInjector: point =>
    {
        if (point == MaaResourceUpdateFaultPoint.AfterResourceFileWrite)
        {
            throw new IOException("write failure");
        }

        if (point == MaaResourceUpdateFaultPoint.DuringRollback)
        {
            throw new IOException("rollback failure");
        }
    });
    var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);

    var result = await fixture.Module.UpdateAsync(fixture.MaaPath, plan, CancellationToken.None);

    Assert.False(result.Succeeded, "回滚失败必须阻止工作流");
    Assert.True(result.RecoveryRequired, "回滚失败必须保留 pending 恢复要求");
    Assert.True(Directory.EnumerateFiles(
            fixture.AppDataRoot,
            "transaction.json",
            SearchOption.AllDirectories).Any(),
        "回滚失败必须保留事务日志");
    var audit = Directory.EnumerateFiles(
        fixture.AppDataRoot,
        "audit.jsonl",
        SearchOption.AllDirectories).Single();
    Assert.True((await File.ReadAllTextAsync(audit)).Contains("RollbackFailed", StringComparison.Ordinal),
        "回滚失败必须保留审计证据");
}

static async Task MaaResourceTamperedRootPathBlocksRecoveryAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area, faultInjector: point =>
    {
        if (point == MaaResourceUpdateFaultPoint.AfterResourceFileWrite)
        {
            throw new MaaResourceSimulatedCrashException("simulated path tampering crash");
        }
    });
    var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    try
    {
        _ = await fixture.Module.UpdateAsync(fixture.MaaPath, plan, CancellationToken.None);
    }
    catch (MaaResourceSimulatedCrashException)
    {
    }

    var transactionPath = Directory.EnumerateFiles(
        fixture.AppDataRoot,
        "transaction.json",
        SearchOption.AllDirectories).Single();
    var transactionJson = await File.ReadAllTextAsync(transactionPath);
    using var transactionDocument = JsonDocument.Parse(transactionJson);
    var stagingPath = transactionDocument.RootElement.GetProperty("StagingRoot").GetString()!;
    var stagingRoot = Path.GetDirectoryName(stagingPath)!;
    var tamperedJson = transactionJson.Replace(
        JsonSerializer.Serialize(stagingPath),
        JsonSerializer.Serialize(stagingRoot),
        StringComparison.Ordinal);
    Assert.False(string.Equals(transactionJson, tamperedJson, StringComparison.Ordinal),
        "测试必须成功篡改事务暂存路径");
    await File.WriteAllTextAsync(transactionPath, tamperedJson);
    var siblingMarker = Path.Combine(stagingRoot, "unrelated-transaction", "keep.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(siblingMarker)!);
    await File.WriteAllTextAsync(siblingMarker, "keep");

    var recoveryModule = new MaaResourceUpdateModule(fixture.Client, fixture.AppDataRoot);
    var recovery = await recoveryModule.RecoverAsync(fixture.MaaPath, CancellationToken.None);

    Assert.Equal(MaaResourceRecoveryKind.Failed, recovery.Kind,
        "事务路径指向暂存根目录时必须阻止恢复");
    Assert.True(File.Exists(siblingMarker),
        "拒绝根目录事务后不得删除其他事务或证据目录");
    Assert.True(File.Exists(transactionPath), "被篡改的事务日志必须保留供人工处理");
}

static async Task MaaResourceCancellationWaitsAfterWriteStartsAsync()
{
    using var area = TestArea.Create();
    var enteredWrite = NewGate();
    using var releaseWrite = new ManualResetEventSlim(false);
    using var fixture = MaaResourceFixture.Create(area, faultInjector: point =>
    {
        if (point == MaaResourceUpdateFaultPoint.BeforeResourceWrite)
        {
            enteredWrite.TrySetResult();
            releaseWrite.Wait();
        }
    });
    using var cancellation = new CancellationTokenSource();
    var plan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    var update = fixture.Module.UpdateAsync(fixture.MaaPath, plan, cancellation.Token);
    await enteredWrite.Task;
    cancellation.Cancel();
    await Task.Delay(50);
    Assert.False(update.IsCompleted, "写入开始后取消必须等待事务达到安全状态");
    releaseWrite.Set();

    var result = await update;
    Assert.True(result.Succeeded, "写入开始后的取消不得打断安全提交");
    Assert.Equal(NewResourceVersion, ReadResourceVersionForTest(fixture.ResourceRoot),
        "等待安全提交后资源版本不正确");
}

static async Task MaaResourceStateIsIsolatedAndPreservesLegacySnapshotAsync()
{
    using var area = TestArea.Create();
    var sharedAppData = Path.Combine(area.Root, "shared-app-data");
    using var first = MaaResourceFixture.Create(area, "MAA 安装一", sharedAppData);
    using var second = MaaResourceFixture.Create(area, "MAA 安装二", sharedAppData);
    var firstPlan = await first.Module.CheckAsync(first.MaaPath, CancellationToken.None);
    var secondPlan = await second.Module.CheckAsync(second.MaaPath, CancellationToken.None);
    Assert.True((await first.Module.UpdateAsync(
        first.MaaPath,
        firstPlan,
        CancellationToken.None)).Succeeded, "第一套 MAA 资源更新失败");
    Assert.True((await second.Module.UpdateAsync(
        second.MaaPath,
        secondPlan,
        CancellationToken.None)).Succeeded, "第二套 MAA 资源更新失败");

    var installationsRoot = Path.Combine(
        sharedAppData,
        "maa-resource-updates",
        "installations");
    var installationAreas = Directory.GetDirectories(installationsRoot);
    Assert.Equal(2, installationAreas.Length, "不同规范化安装路径必须使用不同状态区域");
    var firstInstallationArea = installationAreas.Single(path =>
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(path, "state", "installation-state.json")));
        return string.Equals(
            document.RootElement.GetProperty("InstallationRoot").GetString(),
            first.InstallationRoot,
            StringComparison.OrdinalIgnoreCase);
    });
    var legacySnapshotMarker = Path.Combine(
        firstInstallationArea,
        "rollback",
        "last-known-good",
        "legacy.marker");
    Directory.CreateDirectory(Path.GetDirectoryName(legacySnapshotMarker)!);
    await File.WriteAllTextAsync(legacySnapshotMarker, "keep-existing-snapshot");

    first.AddRemoteRelease(TestCommit('b'), NewerResourceVersion);
    first.Handler.BranchCommit = TestCommit('b');
    var newerPlan = await first.Module.CheckAsync(first.MaaPath, CancellationToken.None);
    Assert.True((await first.Module.UpdateAsync(
        first.MaaPath,
        newerPlan,
        CancellationToken.None)).Succeeded, "同一安装的第二次资源更新失败");
    Assert.Equal("keep-existing-snapshot", await File.ReadAllTextAsync(legacySnapshotMarker),
        "轻量更新不得替换或清理已有 last-known-good 数据");
    Assert.True(HasOnlyLegacySnapshot(firstInstallationArea),
        "成功提交后只应清理本次事务的临时快照");
}

static async Task MaaResourceTemporarySnapshotFailurePreservesLegacySnapshotAsync()
{
    using var area = TestArea.Create();
    using var fixture = MaaResourceFixture.Create(area);
    var initialPlan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    Assert.True((await fixture.Module.UpdateAsync(
        fixture.MaaPath,
        initialPlan,
        CancellationToken.None)).Succeeded, "前置资源更新失败");
    var installationArea = Directory.GetDirectories(Path.Combine(
        fixture.AppDataRoot,
        "maa-resource-updates",
        "installations")).Single();
    var legacySnapshotMarker = Path.Combine(
        installationArea,
        "rollback",
        "last-known-good",
        "legacy.marker");
    Directory.CreateDirectory(Path.GetDirectoryName(legacySnapshotMarker)!);
    await File.WriteAllTextAsync(legacySnapshotMarker, "keep-existing-snapshot");

    fixture.AddRemoteRelease(TestCommit('b'), NewerResourceVersion);
    fixture.Handler.BranchCommit = TestCommit('b');
    var newerPlan = await fixture.Module.CheckAsync(fixture.MaaPath, CancellationToken.None);
    var failingModule = new MaaResourceUpdateModule(
        fixture.Client,
        fixture.AppDataRoot,
        point =>
        {
            if (point == MaaResourceUpdateFaultPoint.AfterBackupPrepared)
            {
                throw new IOException("candidate verification boundary failure");
            }
        });

    var result = await failingModule.UpdateAsync(
        fixture.MaaPath,
        newerPlan,
        CancellationToken.None);

    Assert.False(result.Succeeded, "临时快照准备完成后的故障应阻止更新");
    Assert.Equal(NewResourceVersion, ReadResourceVersionForTest(fixture.ResourceRoot),
        "写入前故障不得改变当前资源版本");
    Assert.Equal("keep-existing-snapshot", await File.ReadAllTextAsync(legacySnapshotMarker),
        "本次临时快照失败不得影响已有 last-known-good 数据");
    Assert.True(HasOnlyLegacySnapshot(installationArea),
        "写入前失败应清理本次事务的临时快照");
}

static async Task MaaResourceChangesDoNotRepeatSessionCheckAsync()
{
    using var area = TestArea.Create();
    var resource = new FakeMaaResourceUpdateModule
    {
        ScopeFingerprint = "RESOURCE-A"
    };
    var program = new FakeMaaProgramUpdateOperations();
    var provider = CreateTestMaaUpdateProvider(resource, program);
    var settings = CreateTestMaaProviderSettings(area);
    settings.UpdateToolsBeforeLaunch = true;
    var coordinator = new ToolUpdateCoordinator([provider], new ToolUpdateStateStore(area.File("receipt-state")));
    var workflow = Workflow((ToolId.Maa, 1));
    var first = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.Maa)], workflow, settings);
    resource.ScopeFingerprint = "RESOURCE-B";

    var second = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.Maa)],
        workflow,
        settings,
        CancellationToken.None);

    Assert.True(second.Succeeded, "资源变化后本地准备应成功");
    Assert.Equal(1, program.ProgramCheckCount,
        "同会话资源变化不应再次检查程序更新");
    Assert.Equal(1, resource.CheckCount,
        "同会话资源变化不应再次联网检查");
}

static async Task LegacyMaaPendingWithoutInstallationIdentityFailsClosedAsync()
{
    using var area = TestArea.Create();
    var resource = new FakeMaaResourceUpdateModule();
    var program = new FakeMaaProgramUpdateOperations
    {
        RecoveryResult = ToolUpdateRecoveryResult.Completed(
            "旧 MAA 程序更新已恢复",
            FakeToolUpdateProvider.Fingerprint("2.0.0"))
    };
    var provider = CreateTestMaaUpdateProvider(resource, program);
    var result = await provider.RecoverAsync(
        CreateTestMaaProviderSettings(area),
        new ToolUpdatePendingState
        {
            ToolId = ToolId.Maa,
            TargetVersion = "2.0.0",
            BeforeFingerprint = FakeToolUpdateProvider.Fingerprint("1.0.0"),
            StartedAt = DateTimeOffset.UtcNow
        },
        CancellationToken.None);

    Assert.Equal(ToolUpdateRecoveryKind.Failed, result.Kind,
        "旧 MAA pending 缺少安装身份时必须停止自动恢复");
    Assert.True(result.Message.Contains("缺少安装路径", StringComparison.Ordinal),
        "旧 MAA pending 缺少安装身份时应提示恢复原路径");
    Assert.Equal(0, program.RecoveryCount, "安装身份验证前不得调用程序恢复逻辑");
    Assert.Equal(0, resource.RecoveryCount, "安装身份验证前不得触碰当前路径的资源恢复");
}

static async Task MaaPendingPathChangeBlocksRecoveryAsync()
{
    using var area = TestArea.Create();
    var resource = new FakeMaaResourceUpdateModule
    {
        CheckPlans = new Queue<MaaResourceUpdatePlan>(
        [
            new MaaResourceUpdatePlan(
                TestCommit('a'),
                OldResourceVersion,
                NewResourceVersion,
                true)
        ])
    };
    var program = new FakeMaaProgramUpdateOperations();
    var provider = CreateTestMaaUpdateProvider(resource, program);
    var settings = CreateTestMaaProviderSettings(area);
    var check = await provider.CheckAsync(settings, CancellationToken.None);
    settings.MaaPath = Path.Combine(area.Root, "另一套 MAA", "MAA.exe");

    var result = await provider.RecoverAsync(
        settings,
        new ToolUpdatePendingState
        {
            ToolId = ToolId.Maa,
            TargetVersion = check.TargetVersion,
            BeforeFingerprint = check.CurrentFingerprint,
            StartedAt = DateTimeOffset.UtcNow,
            ProviderData = check.RecoveryData
        },
        CancellationToken.None);

    Assert.Equal(ToolUpdateRecoveryKind.Failed, result.Kind,
        "MaaPath 改变后必须阻止跨安装恢复");
    Assert.Equal(0, resource.RecoveryCount,
        "路径不匹配时不得把旧事务恢复到当前安装");
}

static async Task LegacyToolUpdateStateWithoutProviderDataLoadsAsync()
{
    using var area = TestArea.Create();
    var store = new ToolUpdateStateStore(area.Root);
    await File.WriteAllTextAsync(store.StatePath, """
        {
          "SchemaVersion": 1,
          "TrustedFingerprints": {
            "BetterGi": {
              "Version": "1.0.0",
              "Sha256": "LEGACY-HASH",
              "TotalLength": 10,
              "CapturedAt": "2026-08-26T00:00:00+00:00"
            }
          },
          "PendingUpdates": {
            "BetterGi": {
              "ToolId": "BetterGi",
              "TargetVersion": "2.0.0",
              "BeforeFingerprint": {
                "Version": "1.0.0",
                "Sha256": "ABCDEF",
                "TotalLength": 10,
                "CapturedAt": "2026-08-26T00:00:00+00:00"
              },
              "StartedAt": "2026-08-26T00:00:00+00:00"
            }
          }
        }
        """);

    var state = await store.LoadAsync();

    Assert.True(state.PendingUpdates.TryGetValue(ToolId.BetterGi, out var pending),
        "旧 pending 更新状态未读取");
    Assert.True(pending!.ProviderData is null, "旧状态缺失 provider 数据时应按 null 兼容读取");
    await store.SaveAsync(state);
    var persisted = await File.ReadAllTextAsync(store.StatePath);
    Assert.False(persisted.Contains("TrustedFingerprints", StringComparison.Ordinal),
        "旧可信指纹字段应在下次正常保存时自然移除");
}

static async Task ToolUpdateStateDirectoryPathThrowsAsync()
{
    using var area = TestArea.Create();
    var store = new ToolUpdateStateStore(area.Root);
    Directory.CreateDirectory(store.StatePath);

    try
    {
        var state = await store.LoadAsync();
        throw new InvalidOperationException(
            $"状态路径是目录时不得返回空 state；实际 pending 数：{state.PendingUpdates.Count}");
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
    }
}

static async Task MissingToolUpdateStateReturnsEmptyAsync()
{
    using var area = TestArea.Create();
    var state = await new ToolUpdateStateStore(area.Root).LoadAsync();

    Assert.Equal(0, state.PendingUpdates.Count,
        "明确不存在的工具更新状态应返回空 state");
}

static async Task CorruptToolUpdateStateReportsInvalidDataAsync()
{
    using var area = TestArea.Create();
    var store = new ToolUpdateStateStore(area.Root);
    await File.WriteAllTextAsync(store.StatePath, "{");

    try
    {
        _ = await store.LoadAsync();
        throw new InvalidOperationException("损坏工具更新状态不得返回正常 state");
    }
    catch (InvalidDataException exception)
    {
        Assert.True(exception.Message.Contains("无法解析", StringComparison.Ordinal),
            $"损坏工具更新状态应保留独立解析错误：{exception.Message}");
    }
}

static async Task ToolUpdateStateAccessErrorBlocksPreparationAsync()
{
    using var area = TestArea.Create();
    var store = new ToolUpdateStateStore(area.Root);
    Directory.CreateDirectory(store.StatePath);
    var provider = new FakeToolUpdateProvider(ToolId.BetterGi);
    var coordinator = new ToolUpdateCoordinator([provider], store);

    var result = await coordinator.PrepareAsync(
        [new PreflightAdapter(ToolId.BetterGi)],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings { UpdateToolsBeforeLaunch = true });

    Assert.False(result.Succeeded,
        "工具更新状态访问错误必须阻断准备，不能按无状态继续");
    Assert.True(result.Issues.Any(issue =>
            issue.Message.Contains("工具更新状态无法恢复", StringComparison.Ordinal)),
        "状态访问错误应由 coordinator 暴露为阻断原因");
    Assert.Equal(0, provider.CheckCount,
        "状态访问错误后不得继续版本检查");
}

static bool HasOnlyLegacySnapshot(string installationArea)
{
    var rollbackRoot = Path.Combine(installationArea, "rollback");
    return Directory.Exists(rollbackRoot)
           && Directory.GetDirectories(rollbackRoot).Select(Path.GetFileName)
               .SequenceEqual(["last-known-good"]);
}

static MaaUpdateProvider CreateTestMaaUpdateProvider(
    IMaaResourceUpdateModule resourceModule,
    IMaaProgramUpdateOperations programOperations) =>
    new(
        releaseClient: new StaticGitHubReleaseClient(),
        updateTimeout: null,
        resourceModule: resourceModule,
        programOperations: programOperations);

static AppSettings CreateTestMaaProviderSettings(TestArea area)
{
    var root = Path.Combine(area.Root, "MAA provider 中文");
    Directory.CreateDirectory(Path.Combine(root, "config"));
    var maaPath = Path.Combine(root, "MAA.exe");
    File.WriteAllBytes(maaPath, [0x4D, 0x5A, 0x00, 0x00]);
    File.WriteAllText(Path.Combine(root, "config", "gui.new.json"), """
        {
          "Update": {
            "CheckOnStartup": true,
            "AutoDownloadUpdatePackage": true,
            "AutoInstallUpdatePackage": true,
            "VersionType": "Stable"
          }
        }
        """);
    return new AppSettings { MaaPath = maaPath, MaaProfile = "Default" };
}

static string ReadResourceVersionForTest(string resourceRoot)
{
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(resourceRoot, "version.json")));
    return document.RootElement.GetProperty("last_updated").GetString()!;
}

static string TestCommit(char value) => new(value, 40);

static FakeToolUpdateProvider CreateBlockingUpdateProvider(ToolId toolId, Task release) =>
    new(toolId, updateAvailable: true)
    {
        UpdateHandler = async (_, _) =>
        {
            await release;
            return FakeToolUpdateProvider.Success(toolId, "2.0.0");
        }
    };

static Task WorkflowAutomationPolicyMatchesExitLifecycleAsync()
{
    var settings = new AppSettings
    {
        ExitAfterWorkflowCompletes = true
    };
    Assert.True(WorkflowAutomationPolicy.ShouldExitAfterCompletion(
            settings, QueueRunResult.AllPlannedTasksCompleted,
            shutdownCancellationRequested: false, historyPersisted: true),
        "真实启动、完整结束且历史落盘后应允许自动退出");
    Assert.False(WorkflowAutomationPolicy.ShouldExitAfterCompletion(
            settings, QueueRunResult.NotAllPlannedTasksCompleted,
            shutdownCancellationRequested: false, historyPersisted: true),
        "存在未完成计划任务时不得自动退出");
    Assert.False(WorkflowAutomationPolicy.ShouldExitAfterCompletion(
            settings, QueueRunResult.AllPlannedTasksCompleted,
            shutdownCancellationRequested: true, historyPersisted: true),
        "关闭应用触发取消时不得反向自动退出");
    Assert.False(WorkflowAutomationPolicy.ShouldExitAfterCompletion(
            settings, QueueRunResult.AllPlannedTasksCompleted,
            shutdownCancellationRequested: false, historyPersisted: false),
        "运行历史未落盘时不得自动退出");
    settings.ExitAfterWorkflowCompletes = false;
    Assert.False(WorkflowAutomationPolicy.ShouldExitAfterCompletion(
            settings, QueueRunResult.AllPlannedTasksCompleted,
            shutdownCancellationRequested: false, historyPersisted: true),
        "自动退出设置关闭时不得退出");
    return Task.CompletedTask;
}

static Task WorkflowAutomationPolicyMatchesCompletionErrorNotificationAsync()
{
    var settings = new AppSettings { ExitAfterWorkflowCompletes = true };
    var endedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
    var completedWithErrorRecords = new[]
    {
        new RunRecord
        {
            ToolId = ToolId.Maa,
            ToolName = "明日方舟 · MAA",
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = endedAt,
            State = RunState.CompletedWithErrors,
            Message = string.Join(
                Environment.NewLine,
                "• 基建换班",
                "• 领取奖励")
        },
        new RunRecord
        {
            ToolId = ToolId.BetterGi,
            ToolName = "原神 · BetterGI",
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = endedAt,
            State = RunState.CompletedWithErrors,
            Message = "• 战斗策略失败"
        },
        new RunRecord
        {
            ToolId = ToolId.MaaEnd,
            ToolName = "终末地 · MaaEnd",
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = endedAt,
            State = RunState.CompletedWithErrors,
            Message = string.Empty
        }
    };

    Assert.False(WorkflowAutomationPolicy.ShouldExitAfterCompletion(
            settings, QueueRunResult.AllPlannedTasksCompletedWithErrors,
            shutdownCancellationRequested: false, historyPersisted: true),
        "全部计划任务完成但存在执行异常时不得自动退出");
    Assert.True(WorkflowAutomationPolicy.ShouldNotifyCompletionWithErrors(
            QueueRunResult.AllPlannedTasksCompletedWithErrors,
            shutdownCancellationRequested: false),
        "全部计划任务完成但存在执行异常时应在整轮结束后提醒用户");
    Assert.False(WorkflowAutomationPolicy.ShouldNotifyCompletionWithErrors(
            QueueRunResult.AllPlannedTasksCompletedWithErrors,
            shutdownCancellationRequested: true),
        "应用关闭或取消时不得反向显示执行异常完成提醒");
    Assert.False(WorkflowAutomationPolicy.ShouldNotifyCompletionWithErrors(
            QueueRunResult.AllPlannedTasksCompleted,
            shutdownCancellationRequested: false),
        "纯成功完成时不应显示执行异常提醒");
    Assert.True(WorkflowAutomationPolicy.ShouldNotifyCompletionWithErrors(
            QueueRunResult.NotAllPlannedTasksCompleted,
            shutdownCancellationRequested: false),
        "未全部完成时应统一提醒补做");
    var expectedMessage = string.Join(
        Environment.NewLine,
        "BetterGI：执行异常，请检查工具",
        "MAA：执行异常，请检查工具",
        "MaaEnd：执行异常，请检查工具");
    Assert.Equal(
        expectedMessage,
        WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(
            QueueRunResult.AllPlannedTasksCompletedWithErrors,
            completedWithErrorRecords,
            shutdownCancellationRequested: false,
            historyPersisted: true),
        "最终提醒只显示软件名、简短状态和操作");
    Assert.Equal(
        "MaaEnd：执行异常，请检查工具",
        WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(
            QueueRunResult.AllPlannedTasksCompletedWithErrors,
            completedWithErrorRecords.Where(record => record.ToolId == ToolId.MaaEnd),
            shutdownCancellationRequested: false,
            historyPersisted: true),
        "最终提醒无明细时应要求检查对应工具，不能编造步骤");
    Assert.Equal(
        string.Join(
            Environment.NewLine,
            expectedMessage,
            "历史保存失败"),
        WorkflowAutomationPolicy.CreateCompletionWithErrorsMessage(
            QueueRunResult.AllPlannedTasksCompletedWithErrors,
            completedWithErrorRecords,
            shutdownCancellationRequested: false,
            historyPersisted: false),
        "最终提醒应同时保留历史保存失败信息");
    return Task.CompletedTask;
}

static async Task LegacyQueueOrderMigratesAsync()
{
    var area = TestArea.Create();
    try
    {
        await File.WriteAllTextAsync(area.File("settings.json"), """
            {
              "MaaProfile": "Legacy",
              "QueueOrder": ["MaaEnd", "BetterGi"]
            }
            """);
        var store = new SettingsStore(area.Root);
        var settings = (await store.LoadAsync()).Settings;
        var workflow = settings.WorkflowTasks
            ?? throw new InvalidOperationException("迁移后缺少工作流。");

        Assert.SequenceEqual(
            new[] { ToolId.MaaEnd, ToolId.BetterGi, ToolId.Maa },
            workflow.Select(task => task.ToolId),
            "旧顺序迁移错误");
        Assert.True(workflow.All(task => task.IsEnabled && task.Channel == 1),
            "旧顺序应迁移为全部启用的通道 1 工作流");

        await store.SaveAsync(settings);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath));
        Assert.True(document.RootElement.TryGetProperty("WorkflowTasks", out _), "新设置应写入 WorkflowTasks");
        Assert.False(document.RootElement.TryGetProperty("QueueOrder", out _), "新设置不应继续写入 QueueOrder");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task WorkflowNormalizesAndPersistsAsync()
{
    var area = TestArea.Create();
    try
    {
        var store = new SettingsStore(area.Root);
        var settings = new AppSettings
        {
            WorkflowTasks =
            [
                new WorkflowTaskSetting { ToolId = ToolId.BetterGi, IsEnabled = false, Channel = 0 },
                new WorkflowTaskSetting { ToolId = ToolId.Maa, IsEnabled = true, Channel = 3 },
                new WorkflowTaskSetting { ToolId = ToolId.Maa, IsEnabled = false, Channel = 2 },
                new WorkflowTaskSetting { ToolId = (ToolId)999, IsEnabled = true, Channel = 9 }
            ]
        };

        await store.SaveAsync(settings);
        var restored = (await store.LoadAsync()).Settings;
        var workflow = restored.WorkflowTasks
            ?? throw new InvalidOperationException("恢复后缺少工作流。");
        Assert.SequenceEqual(new[] { ToolId.BetterGi, ToolId.Maa }, workflow.Select(task => task.ToolId),
            "应移除非法和重复工具，不补回未选择工具");
        Assert.False(workflow[0].IsEnabled, "停用状态未持久化");
        Assert.Equal(1, workflow[0].Channel, "低于范围的通道应归一到 1");
        Assert.Equal(1, workflow[1].Channel, "旧通道 3 应回落到通道 1");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task LegacyThirdChannelMigratesAsync()
{
    using var area = TestArea.Create();
    await File.WriteAllTextAsync(area.File("settings.json"), """
        {"WorkflowTasks":[
          {"ToolId":"MaaEnd","IsEnabled":false,"Channel":3},
          {"ToolId":"BetterGi","IsEnabled":true,"Channel":2},
          {"ToolId":"Maa","IsEnabled":true,"Channel":3}
        ]}
        """);
    var store = new SettingsStore(area.Root);
    var settings = (await store.LoadAsync()).Settings;
    var workflow = settings.WorkflowTasks!;
    Assert.SequenceEqual(new[] { ToolId.MaaEnd, ToolId.BetterGi, ToolId.Maa },
        workflow.Select(task => task.ToolId), "迁移必须保留全部任务和全局顺序");
    Assert.SequenceEqual(new[] { false, true, true }, workflow.Select(task => task.IsEnabled),
        "迁移不能改变启停状态");
    Assert.SequenceEqual(new[] { 1, 2, 1 }, workflow.Select(task => task.Channel),
        "旧通道 3 回落到 1，通道 2 应保留");
    await store.SaveAsync(settings);
    Assert.SequenceEqual(workflow, (await store.LoadAsync()).Settings.WorkflowTasks!,
        "迁移后的工作流必须能够完整往返保存");
}

static Task WorkflowSnapshotPreservesUiMappingAsync()
{
    WorkflowTaskSetting[] rows =
    [
        new WorkflowTaskSetting { ToolId = ToolId.MaaEnd, IsEnabled = true, Channel = 3 },
        new WorkflowTaskSetting { ToolId = ToolId.BetterGi, IsEnabled = false, Channel = 2 },
        new WorkflowTaskSetting { ToolId = ToolId.Maa, IsEnabled = true, Channel = 1 }
    ];

    var snapshot = WorkflowTaskPlan.CreateSnapshot(rows);
    Assert.SequenceEqual(new[] { ToolId.MaaEnd, ToolId.BetterGi, ToolId.Maa },
        snapshot.Select(task => task.ToolId), "任务行全局顺序未映射到工作流");
    Assert.SequenceEqual(new[] { true, false, true }, snapshot.Select(task => task.IsEnabled),
        "任务行启用状态未映射到工作流");
    Assert.SequenceEqual(new[] { 1, 2, 1 }, snapshot.Select(task => task.Channel),
        "任务行通道未映射到工作流");

    var enabled = WorkflowTaskPlan.CreateEnabledSnapshot(rows);
    Assert.SequenceEqual(new[] { ToolId.MaaEnd, ToolId.Maa }, enabled.Select(task => task.ToolId),
        "运行快照应过滤停用项并保持相对顺序");
    return Task.CompletedTask;
}

static Task WorkflowMovesWithinAndAcrossChannelsAsync()
{
    WorkflowTaskSetting[] workflow =
    [
        new WorkflowTaskSetting { ToolId = ToolId.BetterGi, IsEnabled = false, Channel = 1 },
        new WorkflowTaskSetting { ToolId = ToolId.MaaEnd, IsEnabled = true, Channel = 2 },
        new WorkflowTaskSetting { ToolId = ToolId.Maa, IsEnabled = true, Channel = 1 }
    ];

    var unchanged = WorkflowTaskPlan.MoveToChannel(workflow, ToolId.BetterGi, 1, 0);
    Assert.SequenceEqual(workflow.Select(task => task.ToolId), unchanged.Select(task => task.ToolId),
        "停留在通道原位置时不应扰动其他通道的全局映射");

    var reordered = WorkflowTaskPlan.MoveToChannel(workflow, ToolId.Maa, 1, 0);
    Assert.SequenceEqual(new[] { ToolId.Maa, ToolId.BetterGi },
        reordered.Where(task => task.Channel == 1).Select(task => task.ToolId),
        "通道内拖动未更新相对执行顺序");

    var moved = WorkflowTaskPlan.MoveToChannel(reordered, ToolId.BetterGi, 2, 0);
    Assert.SequenceEqual(new[] { ToolId.BetterGi, ToolId.MaaEnd },
        moved.Where(task => task.Channel == 2).Select(task => task.ToolId),
        "跨通道拖动未插入目标位置");
    Assert.False(moved.Single(task => task.ToolId == ToolId.BetterGi).IsEnabled,
        "跨通道拖动不应改变启用状态");
    Assert.Equal(2, moved.Single(task => task.ToolId == ToolId.BetterGi).Channel,
        "跨通道拖动未更新通道");
    var fallback = WorkflowTaskPlan.MoveToChannel(moved, ToolId.BetterGi, 3, 0);
    Assert.Equal(1, fallback.Single(task => task.ToolId == ToolId.BetterGi).Channel,
        "无效目标通道应回落到通道 1");
    Assert.False(fallback.Single(task => task.ToolId == ToolId.BetterGi).IsEnabled,
        "通道回落不应启用停用任务");
    return Task.CompletedTask;
}

static async Task SameChannelRunsSeriallyAsync()
{
    foreach (var channel in new[] { 1, 2 })
    {
        var firstGate = NewGate();
        var secondGate = NewGate();
        var first = new FakeAdapter(ToolId.BetterGi, RunState.Succeeded) { MonitorGate = firstGate.Task };
        var second = new FakeAdapter(ToolId.Maa, RunState.Succeeded) { MonitorGate = secondGate.Task };
        var third = new FakeAdapter(ToolId.MaaEnd, RunState.Succeeded);
        var queueTask = new AutomationQueueService().RunAsync(
            [first, second, third],
            Workflow((ToolId.BetterGi, channel), (ToolId.Maa, channel), (ToolId.MaaEnd, channel)),
            new AppSettings());

        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, second.StartCount, "同通道后一项不应提前启动");
        Assert.Equal(0, third.StartCount, "第三项不应提前启动");
        firstGate.SetResult();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, third.StartCount, "第三项必须等待第二项完成");
        secondGate.SetResult();
        var result = await queueTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, second.StartCount, "同通道前一项完成后应启动后一项");
        Assert.Equal(1, third.StartCount, "第三项应执行一次");
        Assert.Equal(QueueRunResult.AllPlannedTasksCompleted, result, "全部串行完成应计为完整成功");
    }
}

static async Task DifferentChannelsRunInParallelAsync()
{
    var firstGate = NewGate();
    var secondGate = NewGate();
    var first = new FakeAdapter(ToolId.BetterGi, RunState.Succeeded) { MonitorGate = firstGate.Task };
    var second = new FakeAdapter(ToolId.Maa, RunState.Succeeded) { MonitorGate = secondGate.Task };
    var queueTask = new AutomationQueueService().RunAsync(
        [first, second],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)),
        new AppSettings());

    await Task.WhenAll(first.Started.Task, second.Started.Task).WaitAsync(TimeSpan.FromSeconds(2));
    Assert.False(queueTask.IsCompleted, "两个通道应同时处于运行中");
    firstGate.SetResult();
    secondGate.SetResult();
    await queueTask;
}

static async Task SynchronousChannelStartDoesNotBlockOtherChannelsAsync()
{
    using var releaseFirstValidation = new ManualResetEventSlim(false);
    var firstValidationEntered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var first = new FakeAdapter(ToolId.BetterGi, RunState.Succeeded)
    {
        ValidateAction = () =>
        {
            firstValidationEntered.TrySetResult();
            releaseFirstValidation.Wait();
        }
    };
    var second = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
    var queueTask = Task.Run(() => new AutomationQueueService().RunAsync(
        [first, second],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)),
        new AppSettings()));
    var secondStartedWhileFirstBlocked = false;

    try
    {
        await firstValidationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            secondStartedWhileFirstBlocked = !releaseFirstValidation.IsSet;
        }
        catch (TimeoutException)
        {
            // The assertion after cleanup reports the scheduling regression.
        }
    }
    finally
    {
        releaseFirstValidation.Set();
        await queueTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    Assert.True(secondStartedWhileFirstBlocked,
        "通道 1 同步验证阻塞时，通道 2 仍应获得独立启动机会");
    Assert.Equal(1, first.StartCount, "释放通道 1 后应完成其任务");
    Assert.Equal(1, second.StartCount, "通道 2 应且仅应启动一次");
}

static async Task ChannelStartsNextWithoutWaitingAsync()
{
    var firstGate = NewGate();
    var otherChannelGate = NewGate();
    var first = new FakeAdapter(ToolId.BetterGi, RunState.Succeeded) { MonitorGate = firstGate.Task };
    var next = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
    var other = new FakeAdapter(ToolId.MaaEnd, RunState.Succeeded) { MonitorGate = otherChannelGate.Task };
    var queueTask = new AutomationQueueService().RunAsync(
        [first, next, other],
        Workflow((ToolId.BetterGi, 1), (ToolId.MaaEnd, 2), (ToolId.Maa, 1)),
        new AppSettings());

    await Task.WhenAll(first.Started.Task, other.Started.Task).WaitAsync(TimeSpan.FromSeconds(2));
    firstGate.SetResult();
    await next.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.False(otherChannelGate.Task.IsCompleted, "本通道补位不应等待其他通道结束");
    otherChannelGate.SetResult();
    await queueTask;
}

static async Task CompletedWithErrorsContinuesChannelAsync()
{
    ToolId[] tools = [ToolId.BetterGi, ToolId.Maa, ToolId.MaaEnd];
    foreach (var first in tools)
    {
        foreach (var next in tools.Where(tool => tool != first))
        {
            await VerifyCompletedWithErrorsContinuesChannelAsync(
                first, next, tools.Single(tool => tool != first && tool != next));
        }
    }
}

static async Task VerifyCompletedWithErrorsContinuesChannelAsync(ToolId first, ToolId next, ToolId parallel)
{
    var completedWithErrors = new FakeAdapter(first, RunState.CompletedWithErrors);
    var sameChannelNext = new FakeAdapter(next, RunState.Succeeded);
    var otherChannelGate = NewGate();
    var otherChannel = new FakeAdapter(parallel, RunState.Succeeded)
    {
        MonitorGate = otherChannelGate.Task
    };
    var records = new ConcurrentBag<RunRecord>();
    var queue = new AutomationQueueService();
    queue.RunRecorded += record => records.Add(record);

    var queueTask = queue.RunAsync(
        [completedWithErrors, sameChannelNext, otherChannel],
        Workflow((first, 1), (next, 1), (parallel, 2)),
        new AppSettings());

    await Task.WhenAll(completedWithErrors.Started.Task, otherChannel.Started.Task)
        .WaitAsync(TimeSpan.FromSeconds(2));
    await sameChannelNext.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.False(queueTask.IsCompleted, "执行异常通道继续时不应等待其他通道完成后才补位");

    otherChannelGate.SetResult();
    await queueTask;
    Assert.Equal(1, sameChannelNext.StartCount, "执行异常后应自动启动同通道下一项");
    Assert.Equal(1, otherChannel.StartCount, "执行异常不应影响其他通道");
    Assert.True(records.Any(record => record.ToolId == first
        && record.State == RunState.CompletedWithErrors), "执行异常状态应写入本轮运行记录");
}

static async Task SuccessfulQueueReportsAllPlannedTasksCompletedAsync()
{
    var result = await new AutomationQueueService().RunAsync(
        [
            new FakeAdapter(ToolId.BetterGi, RunState.Succeeded),
            new FakeAdapter(ToolId.Maa, RunState.Succeeded)
        ],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 1)),
        new AppSettings());

    Assert.Equal(QueueRunResult.AllPlannedTasksCompleted, result,
        "全部计划任务最终纯成功时应返回纯成功结果");
}

static async Task CompletedWithErrorsQueueReportsDistinctResultAsync()
{
    var result = await new AutomationQueueService().RunAsync(
        [
            new FakeAdapter(ToolId.BetterGi, RunState.CompletedWithErrors),
            new FakeAdapter(ToolId.Maa, RunState.Succeeded),
            new FakeAdapter(ToolId.MaaEnd, RunState.Succeeded)
        ],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 1), (ToolId.MaaEnd, 2)),
        new AppSettings());

    Assert.Equal(QueueRunResult.AllPlannedTasksCompletedWithErrors, result,
        "全部计划任务均已运行结束但存在执行异常时应返回独立结果");
}

static async Task SingleFailureEndChannelReportsIncompleteAsync()
{
    var queue = new AutomationQueueService();

    var result = await queue.RunAsync(
        [new FakeAdapter(ToolId.BetterGi, RunState.Failed)],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings());

    Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, result,
        "单任务失败后结束通道不得计为全部完成");
}

static async Task PartialChannelEndReportsIncompleteAsync()
{
    var completedChannel = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
    var queue = new AutomationQueueService();

    var result = await queue.RunAsync(
        [new FakeAdapter(ToolId.BetterGi, RunState.Failed), completedChannel],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 2)),
        new AppSettings());

    Assert.Equal(1, completedChannel.StartCount, "结束一个通道不应阻止其他通道完成");
    Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, result,
        "任一通道失败结束后整轮不得计为全部完成");
}

static async Task StopAfterCurrentReportsIncompleteAsync()
{
    var gate = NewGate();
    var adapter = new FakeAdapter(ToolId.BetterGi, RunState.Succeeded) { MonitorGate = gate.Task };
    var queue = new AutomationQueueService();
    var runTask = queue.RunAsync(
        [adapter],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings());

    await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.True(queue.StopAfterCurrent(), "运行中的队列应接受停止后续请求");
    gate.SetResult();
    var result = await runTask;

    Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, result,
        "即使当前是最后一个任务，StopAfterCurrent 也不得计为全部完成");
}

static async Task CancelledTaskReportsIncompleteAsync()
{
    var result = await new AutomationQueueService().RunAsync(
        [new FakeAdapter(ToolId.BetterGi, RunState.Cancelled)],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings());

    Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, result,
        "Cancelled 任务不得计为全部完成");
}

static Task CancelledFailedResultDoesNotAdvanceQueueAsync() =>
    AssertCancelledAdapterResultDoesNotAdvanceQueueAsync(RunState.Failed);

static Task CancelledSucceededResultDoesNotAdvanceQueueAsync() =>
    AssertCancelledAdapterResultDoesNotAdvanceQueueAsync(RunState.Succeeded);

static async Task AssertCancelledAdapterResultDoesNotAdvanceQueueAsync(RunState returnedState)
{
    using var cancellation = new CancellationTokenSource();
    var current = new FakeAdapter(ToolId.BetterGi, returnedState)
    {
        BeforeMonitorResult = cancellation.Cancel
    };
    var next = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
    var records = new ConcurrentQueue<RunRecord>();
    var statuses = new ConcurrentQueue<ToolStatusUpdate>();
    var queue = new AutomationQueueService();
    queue.RunRecorded += record => records.Enqueue(record);
    queue.StatusChanged += update => statuses.Enqueue(update);

    var result = await queue.RunAsync(
        [current, next],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 1)),
        new AppSettings(),
        cancellation.Token);

    Assert.Equal(returnedState, records.Single(record => record.ToolId == ToolId.BetterGi).State,
        $"adapter 已返回的 {returnedState} 记录必须保留原终态");
    Assert.Equal(0, next.StartCount, "取消后不得启动同通道下一项");
    Assert.False(statuses.Any(update => update.ToolId == ToolId.Maa && update.State == RunState.Starting),
        "取消后下一项不得发布 Starting");
    Assert.True(statuses.Any(update => update.ToolId == ToolId.Maa
                                      && update.State == RunState.Skipped
                                      && update.Message == "总控监控已取消"),
        "取消后未启动项必须发布“总控监控已取消”的 Skipped 状态");
    Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, result,
        "取消后即使 adapter 返回终态，工作流仍不得计为全部完成");
}

static async Task EmptyQueueReportsIncompleteAsync()
{
    var result = await new AutomationQueueService().RunAsync(
        [],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings());

    Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, result,
        "没有匹配适配器时不存在实际计划任务，不得计为空集成功");
}

static async Task Win32StartFailurePausesOnlyItsChannelAsync()
{
    var startFailure = new FakeAdapter(ToolId.BetterGi, RunState.Succeeded)
    {
        StartException = new Win32Exception(5, "拒绝访问")
    };
    var sameChannelNext = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
    var otherChannel = new FakeAdapter(ToolId.MaaEnd, RunState.Succeeded);
    var records = new ConcurrentBag<RunRecord>();
    var queue = new AutomationQueueService();
    queue.RunRecorded += record => records.Add(record);

    var result = await queue.RunAsync(
        [startFailure, sameChannelNext, otherChannel],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 1), (ToolId.MaaEnd, 2)),
        new AppSettings());

    var failedRecord = records.Single(record => record.State == RunState.Failed);
    Assert.Equal(ToolId.BetterGi, failedRecord.ToolId, "启动异常应记录到对应工具");
    Assert.True(failedRecord.Message.Contains("拒绝访问", StringComparison.Ordinal),
        "失败记录应保留可读的 Win32 启动异常原因");
    Assert.Equal(0, sameChannelNext.StartCount, "结束失败通道后不应启动该通道后一项");
    Assert.Equal(1, otherChannel.StartCount, "Win32 启动异常不应阻止其他通道完成");
    Assert.Equal(1, records.Count(record => record.State == RunState.Succeeded),
        "其他通道应生成一条成功记录");
    Assert.Equal(QueueRunResult.NotAllPlannedTasksCompleted, result,
        "启动失败后结束对应通道不得计为全部完成");
    Assert.False(queue.IsRunning, "队列结束后应清除运行状态");
}

static async Task StopSkipsAllFutureTasksAsync()
{
    var firstGate = NewGate();
    var otherGate = NewGate();
    var first = new FakeAdapter(ToolId.BetterGi, RunState.Succeeded) { MonitorGate = firstGate.Task };
    var future = new FakeAdapter(ToolId.Maa, RunState.Succeeded);
    var otherRunning = new FakeAdapter(ToolId.MaaEnd, RunState.Succeeded) { MonitorGate = otherGate.Task };
    var skipped = new ConcurrentBag<ToolStatusUpdate>();
    var queue = new AutomationQueueService();
    var records = new ConcurrentBag<RunRecord>();
    queue.RunRecorded += records.Add;
    queue.StatusChanged += update =>
    {
        if (update.State == RunState.Skipped)
        {
            skipped.Add(update);
        }
    };
    var queueTask = queue.RunAsync(
        [first, future, otherRunning],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 1), (ToolId.MaaEnd, 2)),
        new AppSettings());

    await Task.WhenAll(first.Started.Task, otherRunning.Started.Task).WaitAsync(TimeSpan.FromSeconds(2));
    Assert.True(queue.StopAfterCurrent(), "首次停止请求应被接受");
    Assert.True(queue.IsStopAfterCurrentRequested, "停止状态应在请求返回时立即可观察");
    Assert.Equal(0, future.StartCount, "停止请求返回前不应启动等待项");
    Assert.True(skipped.Any(update => update.ToolId == ToolId.Maa && update.Channel == 1),
        "停止请求返回前应把等待项标记为跳过");
    var skippedCount = skipped.Count(update => update.ToolId == ToolId.Maa);
    Assert.False(queue.StopAfterCurrent(), "重复停止请求应保持幂等");
    Assert.Equal(skippedCount, skipped.Count(update => update.ToolId == ToolId.Maa),
        "重复停止请求不应重复发布跳过状态");
    Assert.False(firstGate.Task.IsCompleted, "停止后续不应结束正在运行的任务");
    Assert.False(otherGate.Task.IsCompleted, "停止后续不应结束其他通道正在运行的任务");
    firstGate.SetResult();
    otherGate.SetResult();
    await queueTask;
    Assert.Equal(0, future.StartCount, "全局停止后不应启动任何通道的后续项");
    Assert.Equal(1, skipped.Count(update => update.ToolId == ToolId.Maa && update.Channel == 1),
        "未启动项应只带通道信息标记一次跳过");
    Assert.False(queue.IsStopAfterCurrentRequested, "队列结束后应清除停止状态");
    Assert.Equal(1, records.Count(record => record.ToolId == future.Id && record.State == RunState.Skipped), "重复停止与通道收尾只能保存一次跳过历史");
}

static async Task StopAndNextStartRaceIsAtomicAsync()
{
    var firstGate = NewGate();
    var nextGate = NewGate();
    var releaseNextStartingStatus = NewGate();
    var nextStartingStatusSeen = NewGate();
    var skipped = new ConcurrentBag<ToolStatusUpdate>();
    var first = new FakeAdapter(ToolId.BetterGi, RunState.Succeeded) { MonitorGate = firstGate.Task };
    var next = new FakeAdapter(ToolId.Maa, RunState.Succeeded) { MonitorGate = nextGate.Task };
    var future = new FakeAdapter(ToolId.MaaEnd, RunState.Succeeded);
    var queue = new AutomationQueueService();
    queue.StatusChanged += update =>
    {
        if (update.ToolId == ToolId.Maa && update.State == RunState.Starting)
        {
            nextStartingStatusSeen.TrySetResult();
            releaseNextStartingStatus.Task.GetAwaiter().GetResult();
        }

        if (update.State == RunState.Skipped)
        {
            skipped.Add(update);
        }
    };
    var queueTask = queue.RunAsync(
        [first, next, future],
        Workflow((ToolId.BetterGi, 1), (ToolId.Maa, 1), (ToolId.MaaEnd, 1)),
        new AppSettings());

    try
    {
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        firstGate.SetResult();
        await nextStartingStatusSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopTask = Task.Run(queue.StopAfterCurrent);
        Assert.True(await stopTask.WaitAsync(TimeSpan.FromSeconds(2)),
            "状态通知阻塞时停止请求仍应立即返回");
        Assert.True(queue.IsStopAfterCurrentRequested, "竞争边界后应保留停止状态");
        Assert.Equal(0, future.StartCount, "已接受停止请求后不得启动尚未预留的下一项");

        releaseNextStartingStatus.SetResult();
        await next.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, next.StartCount, "停止前已原子预留的当前项应继续运行");
        Assert.False(nextGate.Task.IsCompleted, "停止后续不应结束已预留的当前项");
        nextGate.SetResult();
        await queueTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, future.StartCount, "停止与补位竞争后不得越过停止边界启动后续项");
        Assert.Equal(1, skipped.Count(update => update.ToolId == ToolId.MaaEnd),
            "竞争中未预留的后续项应明确标记为跳过");
    }
    finally
    {
        firstGate.TrySetResult();
        releaseNextStartingStatus.TrySetResult();
        nextGate.TrySetResult();
    }
}

static async Task StopRequestResetsForNextRunAsync()
{
    var firstGate = NewGate();
    var first = new FakeAdapter(ToolId.BetterGi, RunState.Succeeded) { MonitorGate = firstGate.Task };
    var queue = new AutomationQueueService();
    var firstRun = queue.RunAsync(
        [first],
        Workflow((ToolId.BetterGi, 1)),
        new AppSettings());

    await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.True(queue.StopAfterCurrent(), "第一轮应接受停止请求");
    Assert.True(queue.IsStopAfterCurrentRequested, "第一轮运行中应暴露停止状态");
    firstGate.SetResult();
    await firstRun;
    Assert.False(queue.IsStopAfterCurrentRequested, "第一轮结束后应清除停止状态");

    var secondGate = NewGate();
    var second = new FakeAdapter(ToolId.Maa, RunState.Succeeded) { MonitorGate = secondGate.Task };
    var secondRun = queue.RunAsync(
        [second],
        Workflow((ToolId.Maa, 1)),
        new AppSettings());

    await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.False(queue.IsStopAfterCurrentRequested, "新一轮开始时不得继承上一轮停止状态");
    Assert.Equal(1, second.StartCount, "新一轮任务应能正常启动");
    secondGate.SetResult();
    await secondRun;
}

static Task CrashLogWriteCreatesValidJsonLineAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 30, 9, 10, 11, TimeSpan.FromHours(8));
    var exception = CaptureCrashLogException("启动阶段异常");
    var store = new CrashLogStore(area.Root, new FixedTimeProvider(now));

    Assert.True(store.TryWrite("Startup", exception), "崩溃记录写入应成功");

    var crashPath = Path.Combine(area.Root, "crashes", "crash.jsonl");
    var records = ReadCrashLogLines(crashPath);
    Assert.Equal(1, records.Length, "崩溃记录应写入一行");
    var record = records.Single();
    Assert.Equal(now.ToUniversalTime(), record.GetProperty("Timestamp").GetDateTimeOffset(),
        "崩溃时间不正确");
    Assert.Equal("Startup", record.GetProperty("Source").GetString(), "崩溃来源不正确");
    Assert.True(!string.IsNullOrWhiteSpace(record.GetProperty("Version").GetString()),
        "应用版本不应为空");
    Assert.Equal(typeof(InvalidOperationException).FullName,
        record.GetProperty("ExceptionType").GetString(), "异常类型不正确");
    Assert.Equal(exception.Message, record.GetProperty("Message").GetString(), "异常消息不正确");
    Assert.Equal(exception.StackTrace, record.GetProperty("StackTrace").GetString(), "异常堆栈不正确");
    return Task.CompletedTask;
}

static Task CrashLogFieldsRespectLengthLimitsAsync()
{
    using var area = TestArea.Create();
    var store = new CrashLogStore(area.Root, new FixedTimeProvider(DateTimeOffset.UtcNow));
    var exception = new FixedStackException(new string('消', 3_000), new string('栈', 20_000));

    Assert.True(store.TryWrite("Dispatcher", exception), "超长崩溃记录写入应成功");

    var record = ReadCrashLogLines(Path.Combine(area.Root, "crashes", "crash.jsonl")).Single();
    Assert.Equal(2_048, record.GetProperty("Message").GetString()!.Length, "异常消息长度上限不正确");
    Assert.Equal(16_384, record.GetProperty("StackTrace").GetString()!.Length, "异常堆栈长度上限不正确");
    return Task.CompletedTask;
}

static Task CrashLogPruneKeepsSevenDaysAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.FromHours(8));
    var crashPath = Path.Combine(area.Root, "crashes", "crash.jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
    var records = new[]
    {
        CreateCrashLogSeed(now.AddDays(-8), "8 天前"),
        CreateCrashLogSeed(now.AddDays(-6), "6 天前"),
        CreateCrashLogSeed(now, "当前")
    };
    File.WriteAllLines(crashPath, records.Select(record => JsonSerializer.Serialize(record)));
    var store = new CrashLogStore(area.Root, new FixedTimeProvider(now));

    Assert.True(store.TryPrune(), "崩溃记录清理应成功");

    var messages = ReadCrashLogLines(crashPath)
        .Select(record => record.GetProperty("Message").GetString())
        .ToArray();
    Assert.SequenceEqual(new[] { "6 天前", "当前" }, messages!, "崩溃记录保留期不正确");
    return Task.CompletedTask;
}

static Task CorruptCrashLogTailDoesNotBlockWriteAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    var crashPath = Path.Combine(area.Root, "crashes", "crash.jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
    File.WriteAllText(
        crashPath,
        JsonSerializer.Serialize(CreateCrashLogSeed(now.AddMinutes(-1), "原有记录"))
        + Environment.NewLine
        + "{\"Timestamp\":");
    var store = new CrashLogStore(area.Root, new FixedTimeProvider(now));

    Assert.True(store.TryWrite("AppDomain", CaptureCrashLogException("后续记录")),
        "损坏尾行后仍应写入崩溃记录");

    var records = ReadCrashLogLines(crashPath);
    Assert.Equal(2, records.Length, "损坏尾行应被移除且保留两条有效记录");
    Assert.SequenceEqual(new[] { "原有记录", "后续记录" },
        records.Select(record => record.GetProperty("Message").GetString())!,
        "损坏尾行处理后记录顺序不正确");
    return Task.CompletedTask;
}

static Task CrashLogWriteFailsSafelyForUnwritablePathAsync()
{
    using var area = TestArea.Create();
    var blockedRoot = area.File("blocked-root");
    File.WriteAllText(blockedRoot, "该路径是文件，不能作为目录。");
    var store = new CrashLogStore(blockedRoot, new FixedTimeProvider(DateTimeOffset.UtcNow));

    Assert.False(store.TryWrite("Startup", CaptureCrashLogException("写入失败")),
        "不可写路径应返回失败且不抛异常");
    return Task.CompletedTask;
}

static async Task ConcurrentCrashLogWritesRemainValidAsync()
{
    using var area = TestArea.Create();
    var store = new CrashLogStore(area.Root, new FixedTimeProvider(DateTimeOffset.UtcNow));
    const int recordCount = 40;

    var results = await Task.WhenAll(Enumerable.Range(0, recordCount).Select(index => Task.Run(
        () => store.TryWrite("Dispatcher", CaptureCrashLogException($"并发崩溃 {index}")))));

    Assert.True(results.All(result => result), "并发崩溃记录写入均应成功");
    var records = ReadCrashLogLines(Path.Combine(area.Root, "crashes", "crash.jsonl"));
    Assert.Equal(recordCount, records.Length, "并发崩溃记录数量不正确");
    Assert.Equal(recordCount,
        records.Select(record => record.GetProperty("Message").GetString()).Distinct(StringComparer.Ordinal).Count(),
        "并发崩溃记录不应互相拼接、覆盖或重复");
}

static Exception CaptureCrashLogException(string message)
{
    try
    {
        throw new InvalidOperationException(message);
    }
    catch (InvalidOperationException exception)
    {
        return exception;
    }
}

static object CreateCrashLogSeed(DateTimeOffset timestamp, string message) => new
{
    Timestamp = timestamp,
    Source = "Startup",
    Version = "1.0.0",
    ExceptionType = typeof(InvalidOperationException).FullName,
    Message = message,
    StackTrace = "测试堆栈"
};

static JsonElement[] ReadCrashLogLines(string path)
{
    return File.ReadAllLines(path)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        })
        .ToArray();
}

static async Task HistoryReadFiltersExpiredRecordsAndCompactsFileAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(8));
    var history = new HistoryStore(area.Root, new FixedTimeProvider(now));
    Directory.CreateDirectory(Path.GetDirectoryName(history.HistoryPath)!);
    var options = SettingsStore.CreateJsonOptions();
    options.WriteIndented = false;
    var records = new[]
    {
        new RunRecord
        {
            ToolId = ToolId.Maa,
            ToolName = "MAA",
            StartedAt = now.AddDays(-8).AddMinutes(-1),
            EndedAt = now.AddDays(-8),
            State = RunState.Succeeded,
            Message = "8 天前"
        },
        new RunRecord
        {
            ToolId = ToolId.BetterGi,
            ToolName = "BetterGI",
            StartedAt = now.AddDays(-6).AddMinutes(-1),
            EndedAt = now.AddDays(-6),
            State = RunState.Succeeded,
            Message = "6 天前"
        },
        new RunRecord
        {
            ToolId = ToolId.MaaEnd,
            ToolName = "MaaEnd",
            StartedAt = now.AddMinutes(-1),
            EndedAt = now,
            State = RunState.Succeeded,
            Message = "当前"
        }
    };
    var lines = records.Select(record => JsonSerializer.Serialize(record, options));
    await File.WriteAllTextAsync(history.HistoryPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);

    var actual = await history.ReadAllAsync();

    Assert.SequenceEqual(new[] { "当前", "6 天前" }, actual.Select(record => record.Message),
        "读取不应返回 7 天保留期以外的记录");
    var compactedLines = await File.ReadAllLinesAsync(history.HistoryPath);
    var compactedMessages = compactedLines
        .Select(line => JsonSerializer.Deserialize<RunRecord>(line, options)!.Message);
    Assert.SequenceEqual(new[] { "6 天前", "当前" }, compactedMessages,
        "物理压缩应只移除过期记录并保留原文件顺序");
}

static async Task HistoryAppendOmitsRawLogExcerptWithoutMutatingInputAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(8));
    var history = new HistoryStore(area.Root, new FixedTimeProvider(now));
    const string sensitiveMarker = "history-raw-log-sensitive-marker-7e6b90";
    var expected = new RunRecord
    {
        ToolId = ToolId.MaaEnd,
        ToolName = "MaaEnd",
        StartedAt = now.AddMinutes(-2),
        EndedAt = now,
        State = RunState.CompletedWithErrors,
        Message = "整理后的执行异常",
        ExitCode = 23,
        LogExcerpt = new[] { sensitiveMarker },
        WorkflowRunId = Guid.Parse("3b471646-e3ad-46b1-a62f-f01a93ac4570"),
        TaskExecutionId = Guid.Parse("f3cc6fed-d9dc-4ec9-a326-b368ab8ce96e"),
        Channel = 2
    };

    await history.AppendAsync(expected);

    Assert.SequenceEqual(new[] { sensitiveMarker }, expected.LogExcerpt,
        "追加不应改变调用者持有的内存日志摘录");
    var persistedText = await File.ReadAllTextAsync(history.HistoryPath);
    Assert.False(persistedText.Contains(sensitiveMarker, StringComparison.Ordinal),
        "历史文件不应包含外部工具原始日志标记");
    var actual = (await history.ReadAllAsync()).Single();
    Assert.Equal(expected.Id, actual.Id, "历史记录 ID 未保留");
    Assert.Equal(expected.ToolId, actual.ToolId, "历史工具未保留");
    Assert.Equal(expected.ToolName, actual.ToolName, "历史工具名称未保留");
    Assert.Equal(expected.StartedAt, actual.StartedAt, "历史开始时间未保留");
    Assert.Equal(expected.EndedAt, actual.EndedAt, "历史结束时间未保留");
    Assert.Equal(expected.State, actual.State, "历史状态未保留");
    Assert.Equal(expected.Message, actual.Message, "整理后的历史消息未保留");
    Assert.Equal(expected.ExitCode, actual.ExitCode, "历史退出码未保留");
    Assert.Equal(0, actual.LogExcerpt.Count, "读回的历史日志摘录应为空");
    Assert.Equal(expected.WorkflowRunId, actual.WorkflowRunId, "历史工作流 ID 未保留");
    Assert.Equal(expected.TaskExecutionId, actual.TaskExecutionId, "历史任务 ID 未保留");
    Assert.Equal(expected.Channel, actual.Channel, "历史通道未保留");
}

static async Task LegacyHistoryRawLogIsRemovedOnFirstReadAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(8));
    var history = new HistoryStore(area.Root, new FixedTimeProvider(now));
    Directory.CreateDirectory(Path.GetDirectoryName(history.HistoryPath)!);
    const string sensitiveMarker = "legacy-history-sensitive-marker-47c2ac";
    var expected = new RunRecord
    {
        ToolId = ToolId.BetterGi,
        ToolName = "BetterGI",
        StartedAt = now.AddHours(-2),
        EndedAt = now.AddHours(-1),
        State = RunState.Failed,
        Message = "整理后的失败结果",
        ExitCode = 17,
        LogExcerpt = new[] { sensitiveMarker },
        WorkflowRunId = Guid.Parse("761a4718-619f-47fc-ae6a-a0e674823398"),
        TaskExecutionId = Guid.Parse("8e2d9c23-9e76-49bc-b2dc-18dc3b35cc24"),
        Channel = 3
    };
    var options = SettingsStore.CreateJsonOptions();
    options.WriteIndented = false;
    await File.WriteAllTextAsync(
        history.HistoryPath,
        JsonSerializer.Serialize(expected, options) + Environment.NewLine);

    var latest = (await history.ReadLatestAsync(1)).Single();

    Assert.Equal(0, latest.LogExcerpt.Count, "ReadLatestAsync 返回的旧历史日志摘录应为空");
    Assert.Equal(expected.Id, latest.Id, "旧历史记录 ID 未保留");
    Assert.Equal(expected.ToolId, latest.ToolId, "旧历史工具未保留");
    Assert.Equal(expected.ToolName, latest.ToolName, "旧历史工具名称未保留");
    Assert.Equal(expected.StartedAt, latest.StartedAt, "旧历史开始时间未保留");
    Assert.Equal(expected.EndedAt, latest.EndedAt, "旧历史结束时间未保留");
    Assert.Equal(expected.State, latest.State, "旧历史状态未保留");
    Assert.Equal(expected.Message, latest.Message, "旧历史整理后消息未保留");
    Assert.Equal(expected.ExitCode, latest.ExitCode, "旧历史退出码未保留");
    Assert.Equal(expected.WorkflowRunId, latest.WorkflowRunId, "旧历史工作流 ID 未保留");
    Assert.Equal(expected.TaskExecutionId, latest.TaskExecutionId, "旧历史任务 ID 未保留");
    Assert.Equal(expected.Channel, latest.Channel, "旧历史通道未保留");
    var compactedText = await File.ReadAllTextAsync(history.HistoryPath);
    Assert.False(compactedText.Contains(sensitiveMarker, StringComparison.Ordinal),
        "首次读取应从历史文件中移除旧原始日志标记");
    var all = (await history.ReadAllAsync()).Single();
    Assert.Equal(0, all.LogExcerpt.Count, "ReadAllAsync 返回的历史日志摘录应为空");
}

static async Task HistoryAppendCleansExpiredRecordsAndPreservesValidRecordsAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(8));
    var history = new HistoryStore(area.Root, new FixedTimeProvider(now));
    Directory.CreateDirectory(Path.GetDirectoryName(history.HistoryPath)!);
    var options = SettingsStore.CreateJsonOptions();
    options.WriteIndented = false;
    var existingRecords = new[]
    {
        new RunRecord
        {
            ToolId = ToolId.Maa,
            ToolName = "MAA",
            StartedAt = now.AddDays(-8).AddMinutes(-1),
            EndedAt = now.AddDays(-8),
            State = RunState.Succeeded,
            Message = "过期记录"
        },
        new RunRecord
        {
            ToolId = ToolId.BetterGi,
            ToolName = "BetterGI",
            StartedAt = now.AddDays(-2).AddMinutes(-1),
            EndedAt = now.AddDays(-2),
            State = RunState.Succeeded,
            Message = "保留记录"
        }
    };
    var existingLines = existingRecords.Select(record => JsonSerializer.Serialize(record, options));
    await File.WriteAllTextAsync(
        history.HistoryPath,
        string.Join(Environment.NewLine, existingLines) + Environment.NewLine);
    var appended = new RunRecord
    {
        ToolId = ToolId.MaaEnd,
        ToolName = "MaaEnd",
        StartedAt = now.AddMinutes(-2),
        EndedAt = now,
        State = RunState.CompletedWithErrors,
        Message = "新增记录",
        ExitCode = 23,
        LogExcerpt = new[] { "摘录" },
        WorkflowRunId = Guid.Parse("3b471646-e3ad-46b1-a62f-f01a93ac4570"),
        TaskExecutionId = Guid.Parse("f3cc6fed-d9dc-4ec9-a326-b368ab8ce96e"),
        Channel = 2
    };

    await history.AppendAsync(appended);

    var persisted = (await File.ReadAllLinesAsync(history.HistoryPath))
        .Select(line => JsonSerializer.Deserialize<RunRecord>(line, options)!)
        .ToArray();
    Assert.SequenceEqual(new[] { "保留记录", "新增记录" }, persisted.Select(record => record.Message),
        "追加应清理过期记录且不丢失保留期内记录");
    var actualAppended = persisted[1];
    Assert.Equal(appended.Id, actualAppended.Id, "新记录 ID 未保存");
    Assert.Equal(appended.ToolId, actualAppended.ToolId, "新记录工具未保存");
    Assert.Equal(appended.ToolName, actualAppended.ToolName, "新记录工具名称未保存");
    Assert.Equal(appended.StartedAt, actualAppended.StartedAt, "新记录开始时间未保存");
    Assert.Equal(appended.EndedAt, actualAppended.EndedAt, "新记录结束时间未保存");
    Assert.Equal(appended.State, actualAppended.State, "新记录状态未保存");
    Assert.Equal(appended.Message, actualAppended.Message, "新记录消息未保存");
    Assert.Equal(appended.ExitCode, actualAppended.ExitCode, "新记录退出码未保存");
    Assert.Equal(0, actualAppended.LogExcerpt.Count, "新记录日志摘录不应保存");
    Assert.Equal(appended.WorkflowRunId, actualAppended.WorkflowRunId, "新记录工作流 ID 未保存");
    Assert.Equal(appended.TaskExecutionId, actualAppended.TaskExecutionId, "新记录任务 ID 未保存");
    Assert.Equal(appended.Channel, actualAppended.Channel, "新记录通道未保存");
}

static async Task HistoryRetentionKeepsExactSevenDayBoundaryAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(8));
    var cutoff = now.AddDays(-7);
    var history = new HistoryStore(area.Root, new FixedTimeProvider(now));
    Directory.CreateDirectory(Path.GetDirectoryName(history.HistoryPath)!);
    var options = SettingsStore.CreateJsonOptions();
    options.WriteIndented = false;
    var records = new[]
    {
        new RunRecord
        {
            ToolId = ToolId.Maa,
            ToolName = "MAA",
            StartedAt = cutoff.AddMinutes(-1),
            EndedAt = cutoff,
            State = RunState.Succeeded,
            Message = "恰好七天"
        },
        new RunRecord
        {
            ToolId = ToolId.BetterGi,
            ToolName = "BetterGI",
            StartedAt = cutoff.AddMinutes(-1),
            EndedAt = cutoff.AddTicks(-1),
            State = RunState.Succeeded,
            Message = "早一瞬间"
        }
    };
    var lines = records.Select(record => JsonSerializer.Serialize(record, options));
    await File.WriteAllTextAsync(history.HistoryPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);

    var actual = await history.ReadAllAsync();

    Assert.SequenceEqual(new[] { "恰好七天" }, actual.Select(record => record.Message),
        "七天边界应包含等于截止时间的记录并排除更早记录");
}

static async Task HistoryAppendRejectsExpiredIncomingRecordAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(8));
    var history = new HistoryStore(area.Root, new FixedTimeProvider(now));
    await history.AppendAsync(new RunRecord
    {
        ToolId = ToolId.Maa,
        ToolName = "MAA",
        StartedAt = now.AddMinutes(-1),
        EndedAt = now,
        State = RunState.Succeeded,
        Message = "有效记录"
    });
    var historyBeforeExpiredAppend = await File.ReadAllTextAsync(history.HistoryPath);

    await history.AppendAsync(new RunRecord
    {
        ToolId = ToolId.BetterGi,
        ToolName = "BetterGI",
        StartedAt = now.AddDays(-7).AddMinutes(-1),
        EndedAt = now.AddDays(-7).AddTicks(-1),
        State = RunState.Succeeded,
        Message = "新传入的过期记录"
    });

    var historyAfterExpiredAppend = await File.ReadAllTextAsync(history.HistoryPath);
    Assert.Equal(historyBeforeExpiredAppend, historyAfterExpiredAppend,
        "新传入的过期记录不应写入或触发无必要重写");
}

static async Task LegacyHistoryRemainsReadableAsync()
{
    var area = TestArea.Create();
    try
    {
        var historyPath = area.File(Path.Combine("runs", "history.jsonl"));
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
        await File.WriteAllTextAsync(historyPath, """
            {"Id":"61cfaac8-aabc-4e26-8495-f96d5420eaa1","ToolId":"Maa","ToolName":"MAA","StartedAt":"2026-08-12T10:00:00+08:00","EndedAt":"2026-08-12T10:01:00+08:00","State":"Succeeded","Message":"完成"}
            """ + Environment.NewLine);

        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.FromHours(8));
        var records = await new HistoryStore(area.Root, new FixedTimeProvider(now)).ReadAllAsync();
        Assert.Equal(1, records.Count, "旧历史记录数量不正确");
        Assert.Equal(ToolId.Maa, records[0].ToolId, "旧历史工具未恢复");
        Assert.Equal("完成", records[0].Message, "旧历史消息未恢复");
        Assert.Equal(0, records[0].LogExcerpt.Count, "缺少日志摘录字段的旧历史应按空摘录读取");
        Assert.Equal<Guid?>(null, records[0].WorkflowRunId, "旧历史的工作流 ID 应为空");
        Assert.Equal<Guid?>(null, records[0].TaskExecutionId, "旧历史的任务执行 ID 应为空");
        Assert.Equal<int?>(null, records[0].Channel, "旧历史的通道应为空");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task HistoryAppendAfterCorruptTailPreservesNewRecordAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(8));
    var history = new HistoryStore(area.Root, new FixedTimeProvider(now));
    Directory.CreateDirectory(Path.GetDirectoryName(history.HistoryPath)!);
    var options = SettingsStore.CreateJsonOptions();
    options.WriteIndented = false;
    var existing = new RunRecord
    {
        ToolId = ToolId.BetterGi,
        ToolName = "BetterGI",
        StartedAt = now.AddHours(-2),
        EndedAt = now.AddHours(-1),
        State = RunState.Succeeded,
        Message = "旧有效记录"
    };
    var appended = new RunRecord
    {
        ToolId = ToolId.Maa,
        ToolName = "MAA",
        StartedAt = now.AddMinutes(-1),
        EndedAt = now,
        State = RunState.Succeeded,
        Message = "新追加记录"
    };
    await File.WriteAllTextAsync(
        history.HistoryPath,
        JsonSerializer.Serialize(existing, options) + Environment.NewLine + "{\"Id\":");

    await history.AppendAsync(appended);
    var actual = await history.ReadAllAsync();

    Assert.SequenceEqual(["新追加记录", "旧有效记录"], actual.Select(record => record.Message),
        "追加前应清理无换行的残缺尾行，不得与新 JSON 粘连");
    var persisted = (await File.ReadAllLinesAsync(history.HistoryPath))
        .Select(line => JsonSerializer.Deserialize<RunRecord>(line, options)!.Message);
    Assert.SequenceEqual(["旧有效记录", "新追加记录"], persisted,
        "历史快照应按原顺序保留旧记录并追加新记录");
}

static async Task NullHistoryRowIsRemovedAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(8));
    var history = new HistoryStore(area.Root, new FixedTimeProvider(now));
    Directory.CreateDirectory(Path.GetDirectoryName(history.HistoryPath)!);
    var options = SettingsStore.CreateJsonOptions();
    options.WriteIndented = false;
    var valid = new RunRecord
    {
        ToolId = ToolId.MaaEnd,
        ToolName = "MaaEnd",
        StartedAt = now.AddMinutes(-2),
        EndedAt = now.AddMinutes(-1),
        State = RunState.Succeeded,
        Message = "有效记录"
    };
    await File.WriteAllTextAsync(
        history.HistoryPath,
        JsonSerializer.Serialize(valid, options) + Environment.NewLine + "null" + Environment.NewLine);

    var actual = await history.ReadAllAsync();

    Assert.SequenceEqual(["有效记录"], actual.Select(record => record.Message),
        "JSON null 不应作为历史记录返回");
    var persistedLines = await File.ReadAllLinesAsync(history.HistoryPath);
    Assert.Equal(1, persistedLines.Length, "JSON null 无效行应在必要重写时移除");
    Assert.Equal("有效记录",
        JsonSerializer.Deserialize<RunRecord>(persistedLines[0], options)!.Message,
        "移除 JSON null 时应保留有效记录");
}

static async Task HistoryDirectoryPathThrowsAsync()
{
    using var area = TestArea.Create();
    var history = new HistoryStore(area.Root);
    Directory.CreateDirectory(history.HistoryPath);

    Exception? observed = null;
    try
    {
        _ = await history.ReadAllAsync();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        observed = exception;
    }

    Assert.True(observed is not null,
        "history.jsonl 路径是目录时必须传播访问错误，不能返回空历史");
}

static async Task CorruptHistoryRowsDoNotAffectValidRecordsAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(8));
    var history = new HistoryStore(area.Root, new FixedTimeProvider(now));
    Directory.CreateDirectory(Path.GetDirectoryName(history.HistoryPath)!);
    var options = SettingsStore.CreateJsonOptions();
    options.WriteIndented = false;
    var first = new RunRecord
    {
        ToolId = ToolId.Maa,
        ToolName = "MAA",
        StartedAt = now.AddHours(-2),
        EndedAt = now.AddHours(-1),
        State = RunState.Succeeded,
        Message = "有效记录 1"
    };
    var second = new RunRecord
    {
        ToolId = ToolId.BetterGi,
        ToolName = "BetterGI",
        StartedAt = now.AddMinutes(-30),
        EndedAt = now,
        State = RunState.Succeeded,
        Message = "有效记录 2"
    };
    var originalHistory = string.Join(Environment.NewLine,
        JsonSerializer.Serialize(first, options),
        "not-json",
        JsonSerializer.Serialize(second, options),
        "{\"Id\":");
    await File.WriteAllTextAsync(history.HistoryPath, originalHistory);

    var actual = await history.ReadAllAsync();

    Assert.SequenceEqual(new[] { "有效记录 2", "有效记录 1" }, actual.Select(record => record.Message),
        "损坏行和残缺尾行不应影响其他有效记录");
    var persisted = (await File.ReadAllLinesAsync(history.HistoryPath))
        .Select(line => JsonSerializer.Deserialize<RunRecord>(line, options)!.Message);
    Assert.SequenceEqual(["有效记录 1", "有效记录 2"], persisted,
        "发现无效行后应原子重写并仅保留有效记录");
}

static async Task ConcurrentHistoryAppendsDoNotOverwriteAsync()
{
    using var area = TestArea.Create();
    var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(8));
    var history = new HistoryStore(area.Root, new FixedTimeProvider(now));
    const int recordCount = 40;

    await Task.WhenAll(Enumerable.Range(0, recordCount).Select(index => history.AppendAsync(new RunRecord
    {
        ToolId = ToolId.Maa,
        ToolName = "MAA",
        StartedAt = now.AddMinutes(-1).AddTicks(index),
        EndedAt = now.AddTicks(index),
        State = RunState.Succeeded,
        Message = $"并发记录 {index}"
    })));

    var actual = await history.ReadAllAsync();
    Assert.Equal(recordCount, actual.Count, "并发追加后历史记录数量不正确");
    Assert.Equal(recordCount, actual.Select(record => record.Message).Distinct(StringComparer.Ordinal).Count(),
        "并发追加不应覆盖或重复记录");
}

static async Task CompletedWithErrorsHistoryRoundTripAsync()
{
    var area = TestArea.Create();
    try
    {
        var history = new HistoryStore(area.Root);
        await history.AppendAsync(new RunRecord
        {
            ToolId = ToolId.Maa,
            ToolName = "MAA",
            StartedAt = DateTimeOffset.Now.AddMinutes(-1),
            EndedAt = DateTimeOffset.Now,
            State = RunState.CompletedWithErrors,
            Message = "执行异常"
        });

        var rawHistory = await File.ReadAllTextAsync(history.HistoryPath);
        Assert.True(rawHistory.Contains("\"State\":\"CompletedWithErrors\"", StringComparison.Ordinal),
            "执行异常历史应使用字符串枚举保存");
        var records = await history.ReadAllAsync();
        Assert.Equal(RunState.CompletedWithErrors, records.Single().State,
            "执行异常历史状态未正确恢复");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task HistoryLatestLimitKeepsNewestRecordsAsync()
{
    var area = TestArea.Create();
    try
    {
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(8));
        var history = new HistoryStore(area.Root, new FixedTimeProvider(now));
        var sharedStart = now.AddMinutes(-2);
        var sharedEnd = now.AddMinutes(-1);
        var recordsToAppend = new[]
        {
            CreateRecord("B", "00000000-0000-0000-0000-000000000003", sharedStart, sharedEnd),
            CreateRecord("C", "00000000-0000-0000-0000-000000000002", sharedStart, sharedEnd),
            CreateRecord("D", "00000000-0000-0000-0000-000000000004", sharedStart, now),
            CreateRecord("A", "00000000-0000-0000-0000-000000000005", now.AddMinutes(-1), now),
            CreateRecord("E", "00000000-0000-0000-0000-000000000001", now.AddMinutes(-3), sharedEnd)
        };

        var realtimeRecords = new List<RunRecord>();
        foreach (var record in recordsToAppend)
        {
            await history.AppendAsync(record);

            var insertionIndex = realtimeRecords.FindIndex(existing =>
                HistoryStore.LatestFirstComparer.Compare(record, existing) < 0);
            realtimeRecords.Insert(insertionIndex < 0 ? realtimeRecords.Count : insertionIndex, record);
            if (realtimeRecords.Count > 4)
            {
                realtimeRecords.RemoveAt(realtimeRecords.Count - 1);
            }
        }

        var expected = new[] { "A", "D", "C", "B" };
        var records = await history.ReadLatestAsync(4);
        var restartedRecords = await new HistoryStore(area.Root, new FixedTimeProvider(now)).ReadLatestAsync(4);
        Assert.SequenceEqual(expected, realtimeRecords.Select(record => record.Message),
            "实时历史顺序未遵守共享比较器");
        Assert.SequenceEqual(expected, records.Select(record => record.Message),
            "当前历史存储未在排序后应用读取上限");
        Assert.SequenceEqual(expected, restartedRecords.Select(record => record.Message),
            "重启后历史顺序未与实时列表保持一致");

        static RunRecord CreateRecord(
            string name,
            string id,
            DateTimeOffset startedAt,
            DateTimeOffset endedAt) => new()
            {
                Id = Guid.Parse(id),
                ToolId = ToolId.Maa,
                ToolName = "MAA",
                StartedAt = startedAt,
                EndedAt = endedAt,
                State = RunState.Succeeded,
                Message = name
            };
    }
    finally
    {
        area.Dispose();
    }
}

static async Task BetterGiOneDragonWaitsForGlobalCompletionAsync()
{
    var area = TestArea.Create();
    var log = area.File("runtime.log");
    using var process = StartControllableProcess();
    try
    {
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var internalGroupObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new AutomationRunHandle(
            process,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source));
        var monitorTask = new BetterGiAdapter(monitor).MonitorAsync(
            handle,
            new AppSettings
            {
                BetterGiMode = "OneDragon",
                BetterGiProfile = "日常一条龙"
            },
            new DirectProgress<string>(line =>
            {
                if (line.Contains("狗粮批发", StringComparison.Ordinal))
                {
                    internalGroupObserved.TrySetResult();
                }
            }),
            CancellationToken.None);

        await File.AppendAllTextAsync(
            log,
            "[09:34:37 INF] 配置组 \"狗粮批发\" 执行结束" + Environment.NewLine);
        await internalGroupObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.False(monitorTask.IsCompleted,
            "一条龙内部配置组结束后不应提前完成整轮监控");

        await File.AppendAllTextAsync(
            log,
            "[09:45:02 INF] 一条龙和配置组任务结束" + Environment.NewLine);
        var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RunState.Succeeded, result.State,
            "一条龙应在最终全局结束标记出现后成功");
    }
    finally
    {
        await EnsureControllableProcessExitedAsync(process);
        area.Dispose();
    }
}

static async Task BetterGiSingleScriptGroupCompletesAsync()
{
    var result = await RunAdapterLogScenarioWithSettingsAsync(
        monitor => new BetterGiAdapter(monitor),
        new AppSettings
        {
            BetterGiMode = "ScriptGroups",
            BetterGiProfile = "日常委托"
        },
        0,
        "[INF] 配置组 \"日常委托\" 执行结束");

    Assert.Equal(RunState.Succeeded, result.State,
        "单个已选择配置组结束后应完成整轮监控");
}

static async Task BetterGiMultipleScriptGroupsWaitForAllAsync()
{
    var settings = new AppSettings
    {
        BetterGiMode = "ScriptGroups",
        BetterGiProfile = "采集;锄地|活动"
    };
    var partial = await RunAdapterLogScenarioWithSettingsAsync(
        monitor => new BetterGiAdapter(monitor),
        settings,
        0,
        "[INF] 配置组 \"采集\" 执行结束");
    Assert.Equal(RunState.Failed, partial.State,
        "多个配置组只结束第一个时不能判定整轮完成");

    var completeOutOfOrder = await RunAdapterLogScenarioWithSettingsAsync(
        monitor => new BetterGiAdapter(monitor),
        settings,
        0,
        "[INF] 配置组 \"活动\" 执行结束",
        "[INF] 配置组 \"采集\" 执行结束",
        "[INF] 配置组 \"锄地\" 执行结束");
    Assert.Equal(RunState.Succeeded, completeOutOfOrder.State,
        "所有已选择配置组结束后应成功，且不依赖日志顺序");
}

static async Task BetterGiScriptGroupsRequireExactDistinctMatchesAsync()
{
    var area = TestArea.Create();
    var log = area.File("runtime.log");
    using var process = StartControllableProcess();
    try
    {
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var edgeCasesObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new AutomationRunHandle(
            process,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source));
        var monitorTask = new BetterGiAdapter(monitor).MonitorAsync(
            handle,
            new AppSettings
            {
                BetterGiMode = "ScriptGroups",
                BetterGiProfile = "日常|日常加强"
            },
            new DirectProgress<string>(line =>
            {
                if (line.Contains("日常加强版", StringComparison.Ordinal))
                {
                    edgeCasesObserved.TrySetResult();
                }
            }),
            CancellationToken.None);

        await File.AppendAllLinesAsync(log,
        [
            "[INF] 配置组 \"日常\" 执行结束",
            "[INF] 配置组 \"日常\" 执行结束",
            "[INF] 配置组 \"未选择\" 执行结束",
            "[INF] 配置组 \"日常加强版\" 执行结束"
        ]);
        await edgeCasesObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.False(monitorTask.IsCompleted,
            "重复、未选择或仅名称相似的配置组不能提前完成整轮监控");

        await File.AppendAllTextAsync(
            log,
            "[INF] 配置组   “  日常加强  ”   执行结束" + Environment.NewLine);
        var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RunState.Succeeded, result.State,
            "配置组名称两侧的引号和空白不应妨碍精确匹配");
    }
    finally
    {
        await EnsureControllableProcessExitedAsync(process);
        area.Dispose();
    }
}

static async Task BetterGiScriptGroupInternalErrorCompletesWithErrorsAsync()
{
    var result = await RunAdapterLogScenarioWithSettingsAsync(
        monitor => new BetterGiAdapter(monitor),
        new AppSettings
        {
            BetterGiMode = "ScriptGroups",
            BetterGiProfile = "日常委托"
        },
        0,
        "[ERR] 脚本执行异常：战斗策略失败",
        "[INF] 配置组 \"日常委托\" 执行结束");

    Assert.Equal(RunState.CompletedWithErrors, result.State,
        "配置组内部异常后出现可靠结束标记时应记为执行异常");
    Assert.Equal("• 战斗策略失败", result.Message,
        "配置组执行异常应只显示规范化后的用户可读项目");
}

static async Task BetterGiWithoutReliableCompletionFailsAsync()
{
    var result = await RunAdapterLogScenarioWithSettingsAsync(
        monitor => new BetterGiAdapter(monitor),
        new AppSettings
        {
            BetterGiMode = "OneDragon",
            BetterGiProfile = "日常一条龙"
        },
        0,
        "[INF] 配置组 \"狗粮批发\" 执行结束");

    Assert.Equal(RunState.Failed, result.State,
        "程序退出但没有可靠整轮标记时应保留现有失败语义");
}

static async Task BetterGiScriptGroupCheckpointIgnoresOldCompletionsAsync()
{
    var area = TestArea.Create();
    try
    {
        var log = area.File("runtime.log");
        await File.WriteAllLinesAsync(log,
        [
            "[OLD] 配置组 \"采集\" 执行结束",
            "[OLD] 配置组 \"锄地\" 执行结束"
        ]);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var checkpoint = monitor.Capture(source);
        await File.AppendAllTextAsync(log, "[NEW] 配置组 \"采集\" 执行结束" + Environment.NewLine);
        using var handle = new AutomationRunHandle(
            await StartExitedProcessAsync(0),
            DateTimeOffset.Now,
            source,
            checkpoint);
        var result = await new BetterGiAdapter(monitor).MonitorAsync(
            handle,
            new AppSettings
            {
                BetterGiMode = "ScriptGroups",
                BetterGiProfile = "采集;锄地"
            },
            null,
            CancellationToken.None);

        Assert.Equal(RunState.Failed, result.State,
            "检查点之前的配置组结束日志不能补足本轮未完成配置组");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task BetterGiScriptGroupMonitorRunsAreIsolatedAsync()
{
    var firstArea = TestArea.Create();
    var secondArea = TestArea.Create();
    using var firstProcess = StartControllableProcess();
    using var secondProcess = StartControllableProcess();
    try
    {
        var firstLog = firstArea.File("runtime.log");
        var secondLog = secondArea.File("runtime.log");
        await File.WriteAllTextAsync(firstLog, "旧运行日志" + Environment.NewLine);
        await File.WriteAllTextAsync(secondLog, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var adapter = new BetterGiAdapter(monitor);
        var firstSource = new LogSource(firstArea.Root, "*.log");
        var secondSource = new LogSource(secondArea.Root, "*.log");
        var firstHandle = new AutomationRunHandle(
            firstProcess,
            DateTimeOffset.Now,
            firstSource,
            monitor.Capture(firstSource));
        var secondHandle = new AutomationRunHandle(
            secondProcess,
            DateTimeOffset.Now,
            secondSource,
            monitor.Capture(secondSource));

        var firstMonitorTask = adapter.MonitorAsync(
            firstHandle,
            new AppSettings
            {
                BetterGiMode = "ScriptGroups",
                BetterGiProfile = "采集;锄地"
            },
            null,
            CancellationToken.None);
        var secondMonitorTask = adapter.MonitorAsync(
            secondHandle,
            new AppSettings
            {
                BetterGiMode = "ScriptGroups",
                BetterGiProfile = "采集;活动"
            },
            null,
            CancellationToken.None);

        await File.AppendAllLinesAsync(firstLog,
        [
            "[INF] 配置组 \"采集\" 执行结束",
            "[INF] 配置组 \"锄地\" 执行结束"
        ]);
        await File.AppendAllLinesAsync(secondLog,
        [
            "[INF] 配置组 \"活动\" 执行结束",
            "[INF] 配置组 \"采集\" 执行结束"
        ]);

        var results = await Task.WhenAll(firstMonitorTask, secondMonitorTask)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(results.All(result => result.State == RunState.Succeeded),
            "同一适配器的并行监控应分别维护各自配置组完成状态");
    }
    finally
    {
        await EnsureControllableProcessExitedAsync(firstProcess);
        await EnsureControllableProcessExitedAsync(secondProcess);
        firstArea.Dispose();
        secondArea.Dispose();
    }
}

static async Task BetterGiCompletionSemanticsAsync()
{
    var completedWithErrors = await RunAdapterLogScenarioAsync(
        monitor => new BetterGiAdapter(monitor),
        0,
        "[ERR] 脚本执行异常：战斗策略失败",
        "[INF] 一条龙任务结束");
    Assert.Equal(RunState.CompletedWithErrors, completedWithErrors.State,
        "BetterGI 内部异常后完整结束应记为执行异常");
    Assert.Equal("• 战斗策略失败", completedWithErrors.Message,
        "BetterGI 执行异常结果应去除原始日志前缀");

    var scriptGroupFailure = await RunAdapterLogScenarioAsync(
        monitor => new BetterGiAdapter(monitor),
        0,
        "[ERR] 配置组执行失败：日常",
        "[INF] 一条龙任务结束");
    Assert.Equal(RunState.CompletedWithErrors, scriptGroupFailure.State,
        "BetterGI 配置组失败后完整结束仍应记为执行异常");
    Assert.Equal("• 日常", scriptGroupFailure.Message,
        "BetterGI 配置组失败应只显示配置组名称");

    var unnamedInternalError = await RunAdapterLogScenarioAsync(
        monitor => new BetterGiAdapter(monitor),
        0,
        "[ERR] 脚本执行异常：   ",
        "[INF] 一条龙任务结束");
    Assert.Equal(RunState.CompletedWithErrors, unnamedInternalError.State,
        "BetterGI 无可读名称的已知异常仍应保留执行异常状态");
    Assert.Equal(string.Empty, unnamedInternalError.Message,
        "BetterGI 无可读名称时用户消息应为空");

    var blockingFailure = await RunAdapterLogScenarioAsync(
        monitor => new BetterGiAdapter(monitor),
        0,
        "[ERR] 任务启动失败：无法载入配置",
        "[INF] 一条龙任务结束");
    Assert.Equal(RunState.Failed, blockingFailure.State,
        "BetterGI 启动失败应优先判为失败");

    var incomplete = await RunAdapterLogScenarioAsync(
        monitor => new BetterGiAdapter(monitor),
        0,
        "[ERR] 配置组执行失败：日常");
    Assert.Equal(RunState.Failed, incomplete.State,
        "BetterGI 只有内部异常而没有整轮结束证据时仍应失败");
}

static async Task BetterGiTerminalTaskFailureCompletesWithErrorsAsync()
{
    var result = await RunAdapterLogScenarioAsync(
        monitor => new BetterGiAdapter(monitor),
        0,
        "[14:41:40.761] [DBG] [Primary:S1:P55856:T1788244786201] BetterGenshinImpact.GameTask.Common.TaskControl",
        "当前不在地图界面",
        "[14:41:40.762] [WRN] [Primary:S1:P55856:T1788244786201] BetterGenshinImpact.GameTask.Common.TaskControl",
        "传送异常当前不在地图界面",
        "[14:41:41.064] [ERR] [Primary:S1:P55856:T1788244786201] BetterGenshinImpact.GameTask.TaskRunner",
        "传送失败",
        "[14:48:47.952] [INF] [Primary:S1:P55856:T1788244786201] BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel",
        "一条龙和配置组任务结束");

    Assert.Equal(RunState.CompletedWithErrors, result.State,
        "BetterGI 最终 TaskRunner 错误后完整结束应记为执行异常");
    Assert.Equal("• 传送失败", result.Message,
        "BetterGI 最终任务错误应保留简短可读明细");
}

static async Task BetterGiTerminalTaskFailureDetailIsReadableAsync()
{
    var result = await RunAdapterLogScenarioAsync(
        monitor => new BetterGiAdapter(monitor),
        0,
        "[14:46:43.737] [ERR] [Primary:S1:P55856:T1788244786201] BetterGenshinImpact.GameTask.TaskRunner",
        "自动地脉花执行失败: 大地图特征点匹配引发异常：The input arrays should have at least 4 corresponding point sets to calculate Homography",
        "[14:48:47.952] [INF] [Primary:S1:P55856:T1788244786201] BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel",
        "一条龙和配置组任务结束");

    Assert.Equal("• 自动地脉花", result.Message,
        "BetterGI 最终任务错误不应向用户显示技术原因");
}

static async Task BetterGiResinExhaustionRemainsSuccessfulAsync()
{
    var result = await RunAdapterLogScenarioAsync(
        monitor => new BetterGiAdapter(monitor),
        0,
        "[10:43:28.682] [ERR] [Primary:S1:P55856:T1788244786201] BetterGenshinImpact.GameTask.TaskRunner",
        "自动地脉花执行失败: 树脂耗尽，任务结束",
        "[10:45:21.054] [INF] [Primary:S1:P55856:T1788244786201] BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel",
        "一条龙和配置组任务结束");

    Assert.Equal(RunState.Succeeded, result.State,
        "BetterGI 树脂耗尽的预期结束条件不应提示执行异常");
}

static async Task BetterGiSuccessfulRetryRemainsSuccessfulAsync()
{
    var result = await RunAdapterLogScenarioAsync(
        monitor => new BetterGiAdapter(monitor),
        0,
        "[14:41:40.762] [WRN] [Primary:S1:P55856:T1788244786201] BetterGenshinImpact.GameTask.Common.TaskControl",
        "传送异常当前不在地图界面",
        "[14:41:42.015] [INF] [Primary:S1:P55856:T1788244786201] BetterGenshinImpact.GameTask.Common.TaskControl",
        "传送完成",
        "[14:48:47.952] [INF] [Primary:S1:P55856:T1788244786201] BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel",
        "一条龙和配置组任务结束");

    Assert.Equal(RunState.Succeeded, result.State,
        "BetterGI 中间传送失败但重试成功时不应提示执行异常");
}

static async Task BetterGiCompletionDoesNotRequireProcessExitAsync()
{
    await VerifyBetterGiCompletionWithoutSelfExitAsync(
        """{ "CompletionAction": "无" }""",
        "未选择退出动作");
    await VerifyBetterGiCompletionWithoutSelfExitAsync(
        """{ "CompletionAction": "未知动作" }""",
        "退出动作未知");
    await VerifyBetterGiCompletionWithoutSelfExitAsync("{", "配置 JSON 无效");
    await VerifyBetterGiCompletionWithoutSelfExitAsync(null, "配置缺失");
}

static async Task VerifyBetterGiCompletionWithoutSelfExitAsync(string? configJson, string scenario)
{
    var area = TestArea.Create();
    var configDirectory = Path.Combine(area.Root, "User", "OneDragon");
    var configPath = area.File(Path.Combine("User", "OneDragon", "日常.json"));
    var log = area.File("runtime.log");
    using var process = StartControllableProcess();
    try
    {
        if (configJson is not null)
        {
            Directory.CreateDirectory(configDirectory);
            await File.WriteAllTextAsync(configPath, configJson);
        }
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var handle = new AutomationRunHandle(
            process,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source));
        var monitorTask = new BetterGiAdapter(monitor).MonitorAsync(
            handle,
            new AppSettings
            {
                BetterGiPath = area.File("BetterGI.exe"),
                BetterGiMode = "OneDragon",
                BetterGiProfile = "日常"
            },
            null,
            CancellationToken.None);

        await File.AppendAllTextAsync(
            log,
            "[INF] 一条龙任务结束" + Environment.NewLine);

        var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RunState.Succeeded, result.State, $"BetterGI {scenario}时完成标记应结束本轮监控");
        Assert.False(process.HasExited, $"BetterGI {scenario}时不应被要求同步退出程序");
        Assert.Equal<int?>(null, result.ExitCode, $"BetterGI {scenario}且程序保持打开时退出码应为空");
        Assert.Equal("任务已完成。", result.Message,
            $"BetterGI {scenario}时应使用统一完成文案");
    }
    finally
    {
        await EnsureControllableProcessExitedAsync(process);
        area.Dispose();
    }
}

static async Task BetterGiConfiguredSelfExitWaitsForProcessExitAsync()
{
    await VerifyBetterGiConfiguredSelfExitAsync("关闭软件");
    await VerifyBetterGiConfiguredSelfExitAsync("关闭游戏和软件");
}

static async Task VerifyBetterGiConfiguredSelfExitAsync(string completionAction)
{
    var area = TestArea.Create();
    var configDirectory = Path.Combine(area.Root, "User", "OneDragon");
    var configPath = area.File(Path.Combine("User", "OneDragon", "日常.json"));
    var log = area.File("runtime.log");
    using var process = StartControllableProcess();
    try
    {
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "CompletionAction": "{{completionAction}}"
            }
            """);
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var completionObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new AutomationRunHandle(
            process,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source));
        var monitorTask = new BetterGiAdapter(monitor).MonitorAsync(
            handle,
            new AppSettings
            {
                BetterGiPath = area.File("BetterGI.exe"),
                BetterGiMode = "OneDragon",
                BetterGiProfile = "日常"
            },
            new DirectProgress<string>(line =>
            {
                if (line.Contains("一条龙任务结束", StringComparison.Ordinal))
                {
                    completionObserved.TrySetResult();
                }
            }),
            CancellationToken.None);

        await File.AppendAllTextAsync(log, "[INF] 一条龙任务结束" + Environment.NewLine);
        await completionObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.False(monitorTask.IsCompleted,
            $"BetterGI 配置{completionAction}时不能在进程退出前结束监控");

        await ExitControllableProcessAsync(process, 0);
        var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RunState.Succeeded, result.State, "BetterGI 完成后正常退出应成功");
        Assert.Equal(0, result.ExitCode, "BetterGI 正常退出结果应保留退出码");
        Assert.Equal("任务已完成，且程序已正常退出。", result.Message,
            "配置退出自身时应使用统一完成与退出文案");
    }
    finally
    {
        await EnsureControllableProcessExitedAsync(process);
        area.Dispose();
    }
}

static async Task MaaInternalErrorsCompleteWithErrorsAsync()
{
    var lines = new List<string>
    {
        "[2026-08-24 10:40:00.000][ERR][TaskQueueViewModel] 任务出错：基建换班",
        "[2026-08-24 10:40:10.000][ERR][FightTask] 代理失败次数已达上限，任务已停止"
    };
    lines.AddRange(Enumerable.Range(0, 30).Select(index => $"[DBG] 普通运行日志 {index}"));
    lines.Add("[2026-08-24 10:41:01.000][INF][TaskQueueViewModel] 任务已全部完成！");
    var result = await RunAdapterLogScenarioAsync(
        monitor => new MaaAdapter(monitor),
        0,
        lines.ToArray());

    Assert.Equal(RunState.CompletedWithErrors, result.State,
        "MAA 已完整收尾但包含内部任务错误时应记为执行异常");
    Assert.Equal(
        string.Join(
            Environment.NewLine,
            "• 基建换班"),
        result.Message,
        "MAA 执行异常结果应只列出可读任务名并去除原始日志前缀");
    Assert.True(result.LogExcerpt?.Any(line => line.Contains("任务出错", StringComparison.Ordinal)) == true,
        "执行异常结果应保留关键错误日志");
    Assert.True(result.LogExcerpt is { Count: <= 20 }, "保留关键错误日志时仍应遵守摘录上限");
}

static async Task MaaCompletionEvidenceSemanticsAsync()
{
    var succeeded = await RunAdapterLogScenarioAsync(
        monitor => new MaaAdapter(monitor),
        0,
        "[INF][TaskQueueViewModel] 任务已全部完成！");
    Assert.Equal(RunState.Succeeded, succeeded.State,
        "MAA 无内部错误的完整结束应成功");

    var englishSucceeded = await RunAdapterLogScenarioAsync(
        monitor => new MaaAdapter(monitor),
        0,
        "[INF][TaskQueueViewModel] AllTasksCompleted");
    Assert.Equal(RunState.Succeeded, englishSucceeded.State,
        "MAA AllTasksCompleted 应表示无异常完整结束");

    var allTasksFailed = await RunAdapterLogScenarioAsync(
        monitor => new MaaAdapter(monitor),
        0,
        "[ERR][TaskQueueViewModel] AllTasksFailed");
    Assert.Equal(RunState.CompletedWithErrors, allTasksFailed.State,
        "MAA AllTasksFailed 应表示整轮异常完成");
    Assert.Equal(string.Empty, allTasksFailed.Message,
        "MAA AllTasksFailed 只有状态时不应生成用户明细");

    var queueFailed = await RunAdapterLogScenarioAsync(
        monitor => new MaaAdapter(monitor),
        0,
        "[ERR][TaskQueueViewModel] 任务队列执行失败");
    Assert.Equal(RunState.CompletedWithErrors, queueFailed.State,
        "MAA 任务队列执行失败应表示整轮异常完成");
    Assert.Equal(string.Empty, queueFailed.Message,
        "MAA 任务队列执行失败只有状态时不应生成用户明细");

    var unnamedTaskError = await RunAdapterLogScenarioAsync(
        monitor => new MaaAdapter(monitor),
        0,
        "[ERR][TaskQueueViewModel] 任务出错：   ",
        "[INF][TaskQueueViewModel] 任务已全部完成！");
    Assert.Equal(RunState.CompletedWithErrors, unnamedTaskError.State,
        "MAA 无可读名称的已知任务异常仍应保留执行异常状态");
    Assert.Equal(string.Empty, unnamedTaskError.Message,
        "MAA 无可读名称时用户消息应为空");

    var agentFailure = await RunAdapterLogScenarioAsync(
        monitor => new MaaAdapter(monitor),
        0,
        "[ERR][FightTask] 代理失败次数已达上限，任务已停止",
        "[INF][TaskQueueViewModel] 任务已全部完成！");
    Assert.Equal(RunState.CompletedWithErrors, agentFailure.State,
        "MAA 全局代理失败仍应保留执行异常状态");
    Assert.Equal(string.Empty, agentFailure.Message,
        "MAA 全局代理失败没有可靠任务名时不应生成用户明细");

    var errorAfterCompletionInSameBatch = await RunAdapterLogScenarioAsync(
        monitor => new MaaAdapter(monitor),
        0,
        "[INF][TaskQueueViewModel] 任务已全部完成！",
        "[ERR][TaskQueueViewModel] 任务出错：领取奖励");
    Assert.Equal(RunState.CompletedWithErrors, errorAfterCompletionInSameBatch.State,
        "同批日志应先累计全部证据，不能在较早完成行处提前成功");
}

static async Task MaaIncompleteOrAbnormalExitFailsAsync()
{
    var incomplete = await RunAdapterLogScenarioAsync(
        monitor => new MaaAdapter(monitor),
        0,
        "[ERR][TaskQueueViewModel] 任务出错：基建换班");
    Assert.Equal(RunState.Failed, incomplete.State,
        "MAA 内部错误后没有完整结束证据时仍应失败");

    var abnormalExit = await RunAdapterLogScenarioAsync(
        monitor => new MaaAdapter(monitor),
        7,
        "[ERR][TaskQueueViewModel] 任务出错：基建换班",
        "[INF][TaskQueueViewModel] 任务已全部完成！");
    Assert.Equal(RunState.Failed, abnormalExit.State,
        "MAA 非零退出应优先于完整结束和内部错误证据");
}

static async Task OldSuccessIsIgnoredAsync()
{
    var area = TestArea.Create();
    try
    {
        var log = area.File("current.log");
        await File.WriteAllTextAsync(log, "任务已全部完成！" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var checkpoint = monitor.Capture(source);
        var result = await monitor.MonitorCoreAsync(() => false, () => null, DateTimeOffset.Now,
            source, checkpoint,
            line => new LogObservation(RunCompleted: line.Contains("任务已全部完成！")),
            TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(2), null, CancellationToken.None);
        Assert.Equal(RunState.TimedOut, result.State, "旧完成标记不应算作本次完成");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task RotatedLogIsDetectedAsync()
{
    var area = TestArea.Create();
    try
    {
        var oldLog = area.File("001.log");
        await File.WriteAllTextAsync(oldLog, "旧日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var task = monitor.MonitorCoreAsync(() => false, () => null, DateTimeOffset.Now,
            source, monitor.Capture(source),
            line => new LogObservation(RunCompleted: line.Contains("tasks-completed")),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), null, CancellationToken.None);
        await Task.Delay(40);
        await File.WriteAllTextAsync(area.File("002.log"), "kind: tasks-completed" + Environment.NewLine);
        var result = await task;
        Assert.Equal(RunState.Succeeded, result.State, "新轮换日志中的完成标记应被识别");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task MaaEndNestedRuntimeSuccessIsDetectedAsync()
{
    var area = TestArea.Create();
    var log = area.File(Path.Combine("cpp-algo", "debug", "maafw.log"));
    var exited = 0;
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(
            area.Root,
            "*.log",
            IncludeSubdirectories: true,
            FollowRotatedFiles: true);
        var task = monitor.MonitorCoreAsync(
            () => Volatile.Read(ref exited) == 1,
            () => 0,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source),
            ObserveMaaEndMonitorLine,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            null,
            CancellationToken.None);

        await Task.Delay(40);
        await File.AppendAllTextAsync(log,
            MaaEndTaskLine(219252, "Starting", 200000009) + Environment.NewLine
            + MaaEndTaskLine(219252, "Succeeded", 200000009) + Environment.NewLine);
        Volatile.Write(ref exited, 1);

        var result = await task;
        Assert.Equal(RunState.Succeeded, result.State, "MaaEnd 全部任务成功后的正常退出未被识别");
    }
    finally
    {
        if (File.Exists(log))
        {
            File.Delete(log);
        }
        var runtimeDirectory = Path.GetDirectoryName(log)!;
        if (Directory.Exists(runtimeDirectory))
        {
            Directory.Delete(runtimeDirectory, recursive: false);
        }
        var algoDirectory = Path.GetDirectoryName(runtimeDirectory)!;
        if (Directory.Exists(algoDirectory))
        {
            Directory.Delete(algoDirectory, recursive: false);
        }
        area.Dispose();
    }
}

static async Task MaaEndIncompleteRunIsRejectedOnExitAsync()
{
    var area = TestArea.Create();
    var log = area.File("maafw.log");
    var exited = 0;
    try
    {
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log", FollowRotatedFiles: true);
        var task = monitor.MonitorCoreAsync(
            () => Volatile.Read(ref exited) == 1,
            () => 0,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source),
            ObserveMaaEndMonitorLine,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            null,
            CancellationToken.None);

        await Task.Delay(40);
        await File.AppendAllTextAsync(log,
            MaaEndTaskLine(219252, "Starting", 200000009) + Environment.NewLine);
        Volatile.Write(ref exited, 1);

        var result = await task;
        Assert.Equal(RunState.Failed, result.State, "MaaEnd 尚有未完成任务时不应因退出码 0 判为成功");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task MaaEndCurrentSelfStopCompletesMirroredRunAsync()
{
    var area = TestArea.Create();
    try
    {
        var configDirectory = Path.Combine(area.Root, "config");
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(area.File(Path.Combine("config", "mxu-MaaEnd.json")), """
            {
              "instances": [
                {
                  "name": "全套日常",
                  "controllerName": "Win32-Front",
                  "tasks": [
                    {
                      "taskName": "__MXU_KILLPROC__",
                      "enabled": true,
                      "enabledByController": {
                        "Win32-Front": true
                      },
                      "optionValues": {
                        "__MXU_KILLPROC_SELF_OPTION__": {
                          "value": true
                        }
                      }
                    }
                  ]
                }
              ]
            }
            """);

        var log = area.File("runtime.log");
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log", IncludeSubdirectories: true, FollowRotatedFiles: true);
        var checkpoint = monitor.Capture(source);
        await File.AppendAllLinesAsync(log,
        [
            MaaEndTaskLine(50612, "Starting", 200000011, "CloseGamePC"),
            MaaEndTaskLine(200120, "Starting", 200000011, "CloseGamePC"),
            MaaEndTaskLine(200268, "Starting", 200000011, "CloseGamePC"),
            MaaEndTaskLine(50612, "Succeeded", 200000011, "CloseGamePC"),
            MaaEndTaskLine(50612, "Starting", 200001272, "MXU_KILLPROC"),
            MaaEndTaskLine(189848, "Starting", 200001272, "MXU_KILLPROC"),
            MaaEndTaskLine(196568, "Starting", 200001272, "MXU_KILLPROC"),
            MaaEndTaskLine(50612, "Succeeded", 200001272, "MXU_KILLPROC"),
            "2026-08-30 18:14:07 INFO  [App] [self-stop#68d9v8v] 收到停止自身请求"
        ]);

        using var handle = new AutomationRunHandle(
            await StartExitedProcessAsync(0),
            DateTimeOffset.Now,
            source,
            checkpoint);
        var result = await new MaaEndAdapter(monitor).MonitorAsync(
            handle,
            new AppSettings
            {
                MaaEndPath = area.File("MaaEnd.exe"),
                MaaEndInstance = "全套日常"
            },
            null,
            CancellationToken.None);

        Assert.Equal(RunState.Succeeded, result.State,
            "当前 MaaEnd 自身停止标记应覆盖收尾时来不及终结的镜像工作项");
        Assert.Equal("任务已完成，且程序已正常退出。", result.Message,
            "配置自身退出的 MaaEnd 应保留完成并正常退出文案");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task MaaEndRotatedBackupDoesNotReuseOldSuccessAsync()
{
    var area = TestArea.Create();
    var currentLog = area.File("maafw.log");
    var backupLog = area.File("maafw.bak.2026.08.21-09.25.17.000.log");
    var exited = 0;
    try
    {
        await File.WriteAllTextAsync(
            currentLog,
            "kind: tasks-completed" + Environment.NewLine + new string('x', 2_000));
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log", FollowRotatedFiles: true);
        var task = monitor.MonitorCoreAsync(
            () => Volatile.Read(ref exited) == 1,
            () => 0,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source),
            ObserveMaaEndMonitorLine,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            null,
            CancellationToken.None);

        await Task.Delay(40);
        File.Move(currentLog, backupLog);
        await File.WriteAllTextAsync(currentLog,
            MaaEndTaskLine(219252, "Starting", 200000009) + Environment.NewLine);
        Volatile.Write(ref exited, 1);

        var result = await task;
        Assert.Equal(RunState.Failed, result.State, "轮换后的旧完成标记不应算作本次完成");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task MaaEndRotatedBackupReadsUnreadTailAsync()
{
    var area = TestArea.Create();
    var currentLog = area.File("maafw.log");
    var backupLog = area.File("maafw.bak.2026.08.22-09.18.23.674.log");
    try
    {
        await File.WriteAllTextAsync(
            currentLog,
            MaaEndTaskLine(100001, "Failed", 100000001, "OldRun")
            + Environment.NewLine
            + "旧运行已消费内容"
            + Environment.NewLine);

        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log", FollowRotatedFiles: true);
        var checkpoint = monitor.Capture(source);

        await File.AppendAllTextAsync(
            currentLog,
            MaaEndTaskLine(219252, "Starting", 200000009)
            + Environment.NewLine
            + MaaEndTaskLine(219252, "Succeeded", 200000009)
            + Environment.NewLine);
        File.Move(currentLog, backupLog);
        await File.WriteAllTextAsync(currentLog, "轮换后的新活动日志" + Environment.NewLine);

        var result = await monitor.MonitorCoreAsync(
            () => true,
            () => 0,
            DateTimeOffset.Now,
            source,
            checkpoint,
            ObserveMaaEndMonitorLine,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            null,
            CancellationToken.None);

        Assert.Equal(RunState.Succeeded, result.State, "轮换前未消费的成功尾部应从备份日志中补读");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task MaaEndAppCompletionRejectsAbnormalExitAsync()
{
    var normalExit = await RunMaaEndAppCompletionScenarioAsync(0);
    Assert.Equal(RunState.Succeeded, normalExit.State, "应用完成证据配合退出码 0 应判定成功");

    var abnormalExit = await RunMaaEndAppCompletionScenarioAsync(1);
    Assert.Equal(RunState.Failed, abnormalExit.State, "应用完成证据不能覆盖非零退出码");
}

static async Task MaaExitSelfCompletionWaitsForProcessExitAsync()
{
    var area = TestArea.Create();
    var configDirectory = Path.Combine(area.Root, "config");
    var configPath = area.File(Path.Combine("config", "gui.new.json"));
    var log = area.File("runtime.log");
    using var process = StartControllableProcess();
    try
    {
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(configPath, """
            {
              "Configurations": {
                "Default": {
                  "Gui": {
                    "PostActions": "ExitSelf, ExitEmulator"
                  }
                }
              }
            }
            """);
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var lineObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new AutomationRunHandle(
            process,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source));
        var monitorTask = new MaaAdapter(monitor).MonitorAsync(
            handle,
            new AppSettings
            {
                MaaPath = area.File("MAA.exe"),
                MaaProfile = "Default"
            },
            new DirectProgress<string>(line =>
            {
                if (line.Contains("任务已全部完成！", StringComparison.Ordinal))
                {
                    lineObserved.TrySetResult();
                }
            }),
            CancellationToken.None);

        await File.AppendAllTextAsync(
            log,
            "[INF][TaskQueueViewModel] 任务已全部完成！" + Environment.NewLine);
        await lineObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.False(monitorTask.IsCompleted,
            "MAA 已配置 ExitSelf 时不能在进程退出前结束监控");

        await ExitControllableProcessAsync(process, 0);
        var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RunState.Succeeded, result.State, "MAA 完成后正常退出应成功");
        Assert.Equal(0, result.ExitCode, "MAA 正常退出结果应保留退出码");
        Assert.Equal("任务已完成，且程序已正常退出。", result.Message,
            "MAA 配置退出自身时应使用统一完成与退出文案");
    }
    finally
    {
        await EnsureControllableProcessExitedAsync(process);
        if (File.Exists(configPath))
        {
            File.Delete(configPath);
        }
        if (Directory.Exists(configDirectory))
        {
            Directory.Delete(configDirectory, recursive: false);
        }
        area.Dispose();
    }
}

static async Task MaaCompletionWithoutExitSelfDoesNotRequireProcessExitAsync()
{
    var area = TestArea.Create();
    var configDirectory = Path.Combine(area.Root, "config");
    var configPath = area.File(Path.Combine("config", "gui.new.json"));
    var log = area.File("runtime.log");
    using var process = StartControllableProcess();
    try
    {
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(configPath, """
            {
              "Configurations": {
                "Default": {
                  "Gui": {
                    "PostActions": "ExitEmulator"
                  }
                }
              }
            }
            """);
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var handle = new AutomationRunHandle(
            process,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source));
        var monitorTask = new MaaAdapter(monitor).MonitorAsync(
            handle,
            new AppSettings
            {
                MaaPath = area.File("MAA.exe"),
                MaaProfile = "Default"
            },
            null,
            CancellationToken.None);

        await File.AppendAllTextAsync(
            log,
            "[INF][TaskQueueViewModel] 任务已全部完成！" + Environment.NewLine);

        var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RunState.Succeeded, result.State, "MAA 完成标记应结束本轮监控");
        Assert.False(process.HasExited, "MAA 未配置 ExitSelf 时不应被要求同步退出程序");
        Assert.Equal<int?>(null, result.ExitCode, "程序保持打开时退出码应为空");
        Assert.Equal("任务已完成。", result.Message,
            "MAA 未配置退出自身时应使用统一完成文案");
    }
    finally
    {
        await EnsureControllableProcessExitedAsync(process);
        if (File.Exists(configPath))
        {
            File.Delete(configPath);
        }
        if (Directory.Exists(configDirectory))
        {
            Directory.Delete(configDirectory, recursive: false);
        }
        area.Dispose();
    }
}

static async Task MaaEndCompletionWaitsForNormalExitAsync()
    => await VerifyMaaEndCompletionWaitsForNormalExitAsync(false);

static async Task MaaEndRetryRecoveryWaitsForNormalExitAsync()
    => await VerifyMaaEndCompletionWaitsForNormalExitAsync(true);

static async Task VerifyMaaEndCompletionWaitsForNormalExitAsync(bool recoverWithTaskError)
{
    var area = TestArea.Create();
    var configDirectory = Path.Combine(area.Root, "config");
    var configPath = area.File(Path.Combine("config", "mxu-MaaEnd.json"));
    var log = area.File("runtime.log");
    using var process = StartControllableProcess();
    try
    {
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(configPath, """
            {
              "instances": [
                {
                  "name": "当前实例",
                  "controllerName": "Win32-Front",
                  "tasks": [
                    {
                      "taskName": "__MXU_KILLPROC__",
                      "enabled": true,
                      "enabledByController": {
                        "Win32-Front": true
                      },
                      "optionValues": {
                        "__MXU_KILLPROC_SELF_OPTION__": {
                          "value": true
                        }
                      }
                    }
                  ]
                }
              ]
            }
            """);
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log", FollowRotatedFiles: true);
        var lineObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new AutomationRunHandle(
            process,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source));
        var monitorTask = new MaaEndAdapter(monitor).MonitorAsync(
            handle,
            new AppSettings
            {
                MaaEndPath = area.File("MaaEnd.exe"),
                MaaEndInstance = "当前实例"
            },
            new DirectProgress<string>(line =>
            {
                if (line.Contains("自动执行任务完成，关闭自身", StringComparison.Ordinal))
                {
                    lineObserved.TrySetResult();
                }
            }),
            CancellationToken.None);

        if (recoverWithTaskError)
        {
            await File.AppendAllLinesAsync(log,
            [
                "2026-09-08 11:25:33 INFO  [Task] 实例 当前实例: 连接失败，第 1 次重试...",
                "2026-09-08 11:25:35 INFO  [Task] 实例 当前实例: 连接成功，等待 5 秒后继续...",
                MaaEndTaskLine(152176, "Starting", 200000009),
                MaaEndTaskLine(152176, "Failed", 200000009)
            ]);
            await Task.Delay(50);
            Assert.False(monitorTask.IsCompleted, "连接重试和内部任务失败都不能提前结束监控");
        }

        await File.AppendAllTextAsync(log,
            "[INFO] [App] 自动执行任务完成，关闭自身" + Environment.NewLine);
        await lineObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.False(monitorTask.IsCompleted,
            "MaaEnd 进程仍存活时不能仅凭完成标记提前结束监控");

        await ExitControllableProcessAsync(process, 0);
        var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(recoverWithTaskError ? RunState.CompletedWithErrors : RunState.Succeeded,
            result.State, "MaaEnd 完成后正常退出应保留内部异常而不是阻断失败");
        Assert.Equal(0, result.ExitCode, "MaaEnd 正常退出结果应保留退出码");
        if (!recoverWithTaskError)
        {
            Assert.Equal("任务已完成，且程序已正常退出。", result.Message,
                "MaaEnd 配置退出自身时应使用统一完成与退出文案");
        }
    }
    finally
    {
        await EnsureControllableProcessExitedAsync(process);
        area.Dispose();
    }
}

static async Task MaaEndRetryDoesNotImplyCompletionAsync()
{
    const string retry = "2026-09-08 11:25:33 INFO  [Task] 实例 当前实例: 连接失败，第 1 次重试...";
    const string completed = "[INFO] kind: tasks-completed";
    foreach (var (exitCode, lines, expected) in new[]
    {
        (0, new[] { retry, completed }, RunState.Succeeded),
        (0, new[] { retry }, RunState.Failed),
        (9, new[] { retry, completed }, RunState.Failed),
        (0, new[] { retry, "[ERROR][Controller] 连接失败", completed }, RunState.Failed)
    })
    {
        var result = await RunAdapterLogScenarioAsync(monitor => new MaaEndAdapter(monitor), exitCode, lines);
        Assert.Equal(expected, result.State, "连接重试不应影响完成证据、非零退出和最终连接失败的优先级");
    }
}

static async Task MaaEndCompletionWithoutSelfExitDoesNotRequireProcessExitAsync()
{
    var area = TestArea.Create();
    var log = area.File("runtime.log");
    using var process = StartControllableProcess();
    try
    {
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log", FollowRotatedFiles: true);
        var handle = new AutomationRunHandle(
            process,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source));
        var monitorTask = new MaaEndAdapter(monitor).MonitorAsync(
            handle,
            new AppSettings
            {
                MaaEndPath = area.File("MaaEnd.exe"),
                MaaEndInstance = "当前实例"
            },
            null,
            CancellationToken.None);

        await File.AppendAllTextAsync(
            log,
            "[INFO] kind: tasks-completed" + Environment.NewLine);

        var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RunState.Succeeded, result.State, "MaaEnd 完成标记应结束本轮监控");
        Assert.False(process.HasExited, "MaaEnd 未配置自身退出任务时不应等待程序退出");
        Assert.Equal<int?>(null, result.ExitCode, "程序保持打开时退出码应为空");
        Assert.Equal("任务已完成。", result.Message,
            "MaaEnd 未配置退出自身时应使用统一完成文案");
    }
    finally
    {
        await EnsureControllableProcessExitedAsync(process);
        area.Dispose();
    }
}

static async Task MaaEndFailedTaskMapsToLocalizedUserNameAsync()
{
    var area = TestArea.Create();
    try
    {
        var tasksDirectory = Path.Combine(area.Root, "tasks");
        var localesDirectory = Path.Combine(area.Root, "locales", "interface");
        Directory.CreateDirectory(tasksDirectory);
        Directory.CreateDirectory(localesDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(tasksDirectory, "gift-operator.json"),
            """
            {
              "task": [
                {
                  "entry": "GiftOperatorMain",
                  "label": "$task.GiftOperator.label"
                }
              ]
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(localesDirectory, "zh_cn.json"),
            """
            {
              "task.GiftOperator.label": "🎁赠送干员礼物"
            }
            """);

        var result = await RunAdapterLogScenarioWithSettingsAsync(
            monitor => new MaaEndAdapter(monitor),
            new AppSettings { MaaEndPath = area.File("MaaEnd.exe") },
            0,
            MaaEndTaskLine(219252, "Starting", 200000003, "GiftOperatorMain"),
            MaaEndTaskLine(219252, "Failed", 200000003, "GiftOperatorMain"));

        Assert.Equal(RunState.CompletedWithErrors, result.State,
            "MaaEnd 可读名称映射不得改变执行异常状态");
        Assert.Equal("• 赠送干员礼物", result.Message,
            "MaaEnd 应从本次安装元数据解析中文任务名并移除开头装饰符号");
        Assert.False(result.Message.Contains("GiftOperatorMain", StringComparison.Ordinal),
            "MaaEnd 用户消息不得泄露内部 entry");
        Assert.False(result.Message.Contains("200000003", StringComparison.Ordinal),
            "MaaEnd 用户消息不得泄露 task_id");
        Assert.False(result.Message.Contains("Px219252", StringComparison.Ordinal),
            "MaaEnd 用户消息不得泄露进程或节点标识");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task MaaEndDirectTaskLabelIsResolvedAsync()
{
    var result = await RunMaaEndFailedTaskWithMetadataAsync(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["direct-label.json"] = """
                {
                  "task": [
                    {
                      "entry": "GiftOperatorMain",
                      "label": "  ✦领取奖励  "
                    }
                  ]
                }
                """
        },
        localeJson: null);

    Assert.Equal(RunState.CompletedWithErrors, result.State,
        "MaaEnd 直接标签映射不得改变执行异常状态");
    Assert.Equal("• 领取奖励", result.Message,
        "MaaEnd 直接中文标签应去除首尾空白和开头装饰符号");
}

static async Task MaaEndInvalidTaskMetadataFallsBackWithoutDetailsAsync()
{
    var cases = new (string Name, IReadOnlyDictionary<string, string> TaskFiles, string? LocaleJson)[]
    {
        (
            "任务文件缺失",
            new Dictionary<string, string>(StringComparer.Ordinal),
            null),
        (
            "任务 JSON 损坏",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["broken.json"] = "{"
            },
            null),
        (
            "任务字段类型错误",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["wrong-types.json"] = """
                    { "task": [ { "entry": 123, "label": true } ] }
                    """
            },
            null),
        (
            "中文资源键缺失",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["missing-key.json"] = """
                    { "task": [ { "entry": "GiftOperatorMain", "label": "$task.Missing.label" } ] }
                    """
            },
            "{}"),
        (
            "中文资源 JSON 损坏",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["broken-locale.json"] = """
                    { "task": [ { "entry": "GiftOperatorMain", "label": "$task.GiftOperator.label" } ] }
                    """
            },
            "{"),
        (
            "同一 entry 标签冲突",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["first.json"] = """
                    { "task": [ { "entry": "GiftOperatorMain", "label": "赠送干员礼物" } ] }
                    """,
                ["second.json"] = """
                    { "task": [ { "entry": "GiftOperatorMain", "label": "领取奖励" } ] }
                    """
            },
            null)
    };

    foreach (var testCase in cases)
    {
        var result = await RunMaaEndFailedTaskWithMetadataAsync(
            testCase.TaskFiles,
            testCase.LocaleJson);
        Assert.Equal(RunState.CompletedWithErrors, result.State,
            $"{testCase.Name}时 MaaEnd 仍应保留执行异常状态");
        Assert.Equal(string.Empty, result.Message,
            $"{testCase.Name}时 MaaEnd 不得使用内部值兜底");
    }
}

static async Task MaaEndTaskFailureChangesAppCompletionResultAsync()
{
    var result = await RunMaaEndAppCompletionScenarioAsync(
        0,
        MaaEndTaskLine(219252, "Failed", 200000009));
    Assert.Equal(RunState.CompletedWithErrors, result.State,
        "当前运行的规范任务失败应把应用完成结果归约为执行异常");
}

static async Task MaaEndMalformedDuplicateEventIsIgnoredAsync()
{
    var result = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        MaaEndTaskLine(231648, "Starting", 200001315, "MaaTaskerPostStopChild"),
        MaaEndTaskLine(231648, "Succeeded", 200001315, "MaaTaskerPostStopChild"),
        "!! [handle=true] [msg=Tasker.Task.Starting] [details={\"entry\":\"MaaTaskerPostStopChild\",\"task_id\":200001315,\"uuid\":\"MXU-DUMMY\"}]",
        "[2026-08-24][INF][Px231648[Tx60941] [msg=Tasker.Task.Starting] [details={\"task_id\":200001316}]",
        "[2026-08-24][INF][Px231648][Tx60941] [msg=Tasker.Task.Starting] [details={\"entry\":\"Broken\"}]",
        "[2026-08-24][INF][Px231648][Tx60941] [msg=Tasker.Task.Starting] [details={\"entry\":\"Broken\",\"task_id\":200001318");

    Assert.Equal(RunState.Succeeded, result.State,
        "缺少规范进程来源、进程结尾、task_id 或完整 details 的残缺行必须忽略");
}

static async Task MaaEndWorkItemExitReductionAsync()
{
    var succeeded = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        MaaEndTaskLine(219252, "Starting", 200000009),
        MaaEndTaskLine(219252, "Succeeded", 200000009));
    Assert.Equal(RunState.Succeeded, succeeded.State,
        "MaaEnd 规范工作项全部成功且正常退出应成功");

    var completedWithErrors = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        MaaEndTaskLine(219252, "Starting", 200000009),
        MaaEndTaskLine(219252, "Failed", 200000009));
    Assert.Equal(RunState.CompletedWithErrors, completedWithErrors.State,
        "MaaEnd 规范工作项以 Failed 终结且正常退出应记为执行异常");
    Assert.Equal(string.Empty, completedWithErrors.Message,
        "MaaEnd 无法从安装元数据解析任务名时不应显示内部值");

    var incomplete = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        MaaEndTaskLine(219252, "Starting", 200000009));
    Assert.Equal(RunState.Failed, incomplete.State,
        "MaaEnd 有真正未终结的规范工作项时应失败");

    var abnormalExit = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        3,
        MaaEndTaskLine(219252, "Starting", 200000009),
        MaaEndTaskLine(219252, "Succeeded", 200000009));
    Assert.Equal(RunState.Failed, abnormalExit.State,
        "MaaEnd 非零退出应优先于全部工作项成功证据");

    var processCollision = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        MaaEndTaskLine(219252, "Starting", 200000009),
        MaaEndTaskLine(237292, "Starting", 200000009),
        MaaEndTaskLine(219252, "Succeeded", 200000009));
    Assert.Equal(RunState.Failed, processCollision.State,
        "不同进程的相同 task_id 必须保持隔离");

    var noisyFailureLines = new List<string>
    {
        MaaEndTaskLine(219252, "Starting", 200000100),
        MaaEndTaskLine(219252, "Failed", 200000100)
    };
    for (var index = 0; index < 15; index++)
    {
        noisyFailureLines.Add(MaaEndTaskLine(219252, "Starting", 200000200 + index));
        noisyFailureLines.Add(MaaEndTaskLine(219252, "Succeeded", 200000200 + index));
    }
    noisyFailureLines.Add("[INFO][App] 自动执行任务完成，关闭自身");
    var noisyFailure = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        noisyFailureLines.ToArray());
    Assert.Equal(RunState.CompletedWithErrors, noisyFailure.State,
        "MaaEnd 后续工作项较多时仍应保留执行异常状态");
    Assert.True(noisyFailure.LogExcerpt?.Any(line =>
            line.Contains("msg=Tasker.Task.Failed", StringComparison.Ordinal)) == true,
        "大量普通工作项证据不应挤出关键失败日志");
    Assert.True(noisyFailure.LogExcerpt is { Count: <= 20 },
        "保留 MaaEnd 关键失败日志时仍应遵守摘录上限");
}

static async Task MaaEndCompletionEvidencePriorityAsync()
{
    const string currentSelfStop =
        "2026-08-30 18:14:07 INFO  [App] [self-stop#68d9v8v] 收到停止自身请求";
    var connectionFailure = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        "[INFO][App] 自动执行任务完成，关闭自身",
        "[ERROR][Controller] 连接失败");
    Assert.Equal(RunState.Failed, connectionFailure.State,
        "MaaEnd 应用完成证据不能覆盖连接失败");

    var taskFailureAfterCompletion = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        "[INFO][App] 自动执行任务完成，关闭自身",
        MaaEndTaskLine(219252, "Failed", 200000009));
    Assert.Equal(RunState.CompletedWithErrors, taskFailureAfterCompletion.State,
        "应用完成证据后的规范任务失败应记为执行异常");

    var abnormalExit = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        9,
        "[INFO][App] 自动执行任务完成，关闭自身");
    Assert.Equal(RunState.Failed, abnormalExit.State,
        "MaaEnd 应用完成证据不能覆盖非零退出");

    var currentSelfStopConnectionFailure = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        currentSelfStop,
        "[ERROR][Controller] 连接失败");
    Assert.Equal(RunState.Failed, currentSelfStopConnectionFailure.State,
        "MaaEnd 当前自身停止证据不能覆盖连接失败");

    var currentSelfStopTaskFailure = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        currentSelfStop,
        MaaEndTaskLine(219252, "Failed", 200000009));
    Assert.Equal(RunState.CompletedWithErrors, currentSelfStopTaskFailure.State,
        "MaaEnd 当前自身停止证据仍应保留任务执行异常");

    var currentSelfStopAbnormalExit = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        9,
        currentSelfStop);
    Assert.Equal(RunState.Failed, currentSelfStopAbnormalExit.State,
        "MaaEnd 当前自身停止证据不能覆盖非零退出");

    var unrelatedSelfStopText = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        MaaEndTaskLine(219252, "Starting", 200000009),
        "[INFO][App] 收到停止自身请求");
    Assert.Equal(RunState.Failed, unrelatedSelfStopText.State,
        "缺少结构化 self-stop 来源的相似文案不得误判整轮完成");

    var frameworkFailure = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        "[ERROR][Task] kind: tasks-failed");
    Assert.Equal(RunState.CompletedWithErrors, frameworkFailure.State,
        "MaaEnd tasks-failed 应表示整轮异常完成");
    Assert.Equal(string.Empty, frameworkFailure.Message,
        "MaaEnd tasks-failed 没有可读任务名时用户消息应为空");

    var frameworkError = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        "[ERROR][Task] kind: tasks-error");
    Assert.Equal(RunState.CompletedWithErrors, frameworkError.State,
        "MaaEnd tasks-error 应表示整轮异常完成");
    Assert.Equal(string.Empty, frameworkError.Message,
        "MaaEnd tasks-error 没有可读任务名时用户消息应为空");

    var malformedTaskFailure = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        "[INFO][App] 自动执行任务完成，关闭自身",
        "!! [msg=Tasker.Task.Failed] [details={\"task_id\":200000009}]");
    Assert.Equal(RunState.Succeeded, malformedTaskFailure.State,
        "缺少规范进程来源的 Tasker 失败事件必须整体忽略");
}

static async Task MaaEndPostStopAloneIsNotCompletionAsync()
{
    var result = await RunAdapterLogScenarioAsync(
        monitor => new MaaEndAdapter(monitor),
        0,
        MaaEndTaskLine(219252, "Starting", 200001315, "MaaTaskerPostStop"),
        MaaEndTaskLine(219252, "Succeeded", 200001315, "MaaTaskerPostStop"));

    Assert.Equal(RunState.Failed, result.State,
        "MaaTaskerPostStop 单独配对不能证明整轮自动化完成");
}

static async Task<RunResult> RunMaaEndAppCompletionScenarioAsync(int exitCode, string? trailingLine = null)
{
    var area = TestArea.Create();
    var log = area.File("2026-08-22-3.log");
    try
    {
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log", FollowRotatedFiles: true);
        var checkpoint = monitor.Capture(source);
        var newContent = "[INFO] [App] 自动执行任务完成，关闭自身" + Environment.NewLine;
        if (trailingLine is not null)
        {
            newContent += trailingLine + Environment.NewLine;
        }

        await File.AppendAllTextAsync(log, newContent);
        return await monitor.MonitorCoreAsync(
            () => true,
            () => exitCode,
            DateTimeOffset.Now,
            source,
            checkpoint,
            ObserveMaaEndMonitorLine,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            null,
            CancellationToken.None);
    }
    finally
    {
        area.Dispose();
    }
}

static async Task MaaEndNestedRuntimeErrorCompletionIsDetectedAsync()
{
    var area = TestArea.Create();
    var log = area.File(Path.Combine("cpp-algo", "debug", "maafw.log"));
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log", IncludeSubdirectories: true);
        var task = monitor.MonitorCoreAsync(
            () => false,
            () => null,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source),
            ObserveMaaEndMonitorLine,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            null,
            CancellationToken.None);

        await Task.Delay(40);
        await File.AppendAllTextAsync(log,
            "[ERROR][Task] kind: tasks-error" + Environment.NewLine);

        var result = await task;
        Assert.Equal(RunState.CompletedWithErrors, result.State,
            "MaaEnd 子目录中的整轮异常完成事件未被识别");
    }
    finally
    {
        if (File.Exists(log))
        {
            File.Delete(log);
        }
        var runtimeDirectory = Path.GetDirectoryName(log)!;
        if (Directory.Exists(runtimeDirectory))
        {
            Directory.Delete(runtimeDirectory, recursive: false);
        }
        var algoDirectory = Path.GetDirectoryName(runtimeDirectory)!;
        if (Directory.Exists(algoDirectory))
        {
            Directory.Delete(algoDirectory, recursive: false);
        }
        area.Dispose();
    }
}

static LogObservation ObserveMaaEndMonitorLine(string line)
{
    var completedWithErrors = line.Contains("kind: tasks-failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("kind: tasks-error", StringComparison.OrdinalIgnoreCase);
    var failedWorkItemId = GetMaaEndTaskEventId(line, "msg=Tasker.Task.Failed");
    return new LogObservation(
        RunCompleted: completedWithErrors
            || line.Contains("kind: tasks-completed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("自动执行任务完成，关闭自身", StringComparison.Ordinal),
        InternalError: completedWithErrors || failedWorkItemId is not null,
        BlockingFailure: line.Contains("连接失败", StringComparison.Ordinal),
        StartedWorkItemId: GetMaaEndTaskEventId(line, "msg=Tasker.Task.Starting"),
        SucceededWorkItemId: GetMaaEndTaskEventId(line, "msg=Tasker.Task.Succeeded"),
        FailedWorkItemId: failedWorkItemId);
}

static string? GetMaaEndTaskEventId(string line, string eventMarker)
{
    var eventStart = line.IndexOf(eventMarker, StringComparison.Ordinal);
    var processStart = eventStart < 0
        ? -1
        : line.LastIndexOf("[Px", eventStart, StringComparison.Ordinal);
    if (processStart < 0)
    {
        return null;
    }

    var processEnd = line.IndexOf(']', processStart);
    if (processEnd < 0 || processEnd > eventStart)
    {
        return null;
    }

    const string taskIdMarker = "\"task_id\":";
    var taskIdStart = line.IndexOf(taskIdMarker, StringComparison.Ordinal);
    if (taskIdStart < 0)
    {
        return null;
    }

    taskIdStart += taskIdMarker.Length;
    var taskIdEnd = taskIdStart;
    while (taskIdEnd < line.Length && char.IsDigit(line[taskIdEnd]))
    {
        taskIdEnd++;
    }

    return taskIdEnd == taskIdStart
        ? null
        : $"{line[(processStart + 1)..processEnd]}:{line[taskIdStart..taskIdEnd]}";
}

static string MaaEndTaskLine(int processId, string state, int taskId, string entry = "DailyRewardStart") =>
    $"[2026-08-24 10:49:58.349][INF][Px{processId}][Tx60941][Utils/EventDispatcher.hpp][L65]"
    + $"[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Tasker.Task.{state}] "
    + $"[details={{\"entry\":\"{entry}\",\"hash\":\"5304331c4baa5342\",\"task_id\":{taskId},\"uuid\":\"MXU-DUMMY\"}}]";

static async Task<RunResult> RunMaaEndFailedTaskWithMetadataAsync(
    IReadOnlyDictionary<string, string> taskFiles,
    string? localeJson)
{
    var area = TestArea.Create();
    try
    {
        if (taskFiles.Count > 0)
        {
            var tasksDirectory = Path.Combine(area.Root, "tasks");
            Directory.CreateDirectory(tasksDirectory);
            foreach (var taskFile in taskFiles)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(tasksDirectory, taskFile.Key),
                    taskFile.Value);
            }
        }

        if (localeJson is not null)
        {
            var localesDirectory = Path.Combine(area.Root, "locales", "interface");
            Directory.CreateDirectory(localesDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(localesDirectory, "zh_cn.json"),
                localeJson);
        }

        return await RunAdapterLogScenarioWithSettingsAsync(
            monitor => new MaaEndAdapter(monitor),
            new AppSettings { MaaEndPath = area.File("MaaEnd.exe") },
            0,
            MaaEndTaskLine(219252, "Starting", 200000003, "GiftOperatorMain"),
            MaaEndTaskLine(219252, "Failed", 200000003, "GiftOperatorMain"));
    }
    finally
    {
        area.Dispose();
    }
}

static async Task CompletionThenAbnormalExitFailsAsync()
{
    var result = await RunCompletionFinalizationScenarioAsync(
        exitCode: 7,
        noLogTimeout: TimeSpan.FromSeconds(1),
        hardTimeout: TimeSpan.FromSeconds(2),
        cancelAfterCompletion: false);

    Assert.Equal(RunState.Failed, result.State, "完成标记不能覆盖稍后的非零退出");
    Assert.Equal(7, result.ExitCode, "异常退出结果应保留退出码");
}

static async Task CompletionMessageFollowsFinalizationPolicyAsync()
{
    var evidenceOnlyMessage = await RunScenarioAsync(CompletionFinalizationPolicy.CompleteOnEvidence);
    var exitRequiredMessage = await RunScenarioAsync(CompletionFinalizationPolicy.RequireProcessExit);

    Assert.Equal("任务已完成。", evidenceOnlyMessage,
        "程序已经退出也不应为未配置退出自身的任务追加退出说明");
    Assert.Equal("任务已完成，且程序已正常退出。", exitRequiredMessage,
        "显式要求退出时应追加统一的正常退出说明");

    static async Task<string> RunScenarioAsync(CompletionFinalizationPolicy policy)
    {
        var area = TestArea.Create();
        try
        {
            var log = area.File("runtime.log");
            await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
            var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
            var source = new LogSource(area.Root, "*.log");
            var checkpoint = monitor.Capture(source);
            await File.AppendAllTextAsync(log, "run-completed" + Environment.NewLine);

            var result = await monitor.MonitorCoreAsync(
                () => true,
                () => 0,
                DateTimeOffset.Now,
                source,
                checkpoint,
                line => new LogObservation(
                    RunCompleted: line.Contains("run-completed", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                null,
                CancellationToken.None,
                policy);
            Assert.Equal(RunState.Succeeded, result.State, "完成证据配合退出码 0 应成功");
            return result.Message;
        }
        finally
        {
            area.Dispose();
        }
    }
}

static async Task BlockingFailureAfterCompletionStillFailsAsync()
{
    var result = await RunCompletionFinalizationScenarioAsync(
        exitCode: null,
        noLogTimeout: TimeSpan.FromSeconds(1),
        hardTimeout: TimeSpan.FromSeconds(2),
        cancelAfterCompletion: false,
        "blocking-failure");

    Assert.Equal(RunState.Failed, result.State, "完成后的阻断日志必须优先判失败");
}

static async Task InternalErrorAfterCompletionCompletesWithErrorsAsync()
{
    var result = await RunCompletionFinalizationScenarioAsync(
        exitCode: 0,
        noLogTimeout: TimeSpan.FromSeconds(1),
        hardTimeout: TimeSpan.FromSeconds(2),
        cancelAfterCompletion: false,
        "internal-error");

    Assert.Equal(RunState.CompletedWithErrors, result.State,
        "完成后的内部错误应在正常退出后归为执行异常");
    Assert.Equal(0, result.ExitCode, "执行异常结果应保留正常退出码");
}

static async Task InternalErrorDetailsDeduplicateAndReportOverflowAsync()
{
    var exactlyEight = await RunAdapterLogScenarioAsync(
        monitor => new BetterGiAdapter(monitor),
        0,
        "[ERR] 脚本执行异常：项目一",
        "[ERR] 脚本执行异常：项目二",
        "[ERR] 脚本执行异常：项目三",
        "[ERR] 脚本执行异常：项目四",
        "[ERR] 脚本执行异常：项目五",
        "[ERR] 脚本执行异常：项目六",
        "[ERR] 脚本执行异常：项目七",
        "[ERR] 脚本执行异常：项目八",
        "[INF] 一条龙任务结束");
    Assert.Equal(
        string.Join(
            Environment.NewLine,
            "• 项目一",
            "• 项目二",
            "• 项目三",
            "• 项目四",
            "• 项目五",
            "• 项目六",
            "• 项目七",
            "• 项目八"),
        exactlyEight.Message,
        "8 个不同异常项应全部显示且不增加溢出说明");

    var overflow = await RunAdapterLogScenarioAsync(
        monitor => new BetterGiAdapter(monitor),
        0,
        "[ERR] 脚本执行异常：项目二",
        "[ERR] 脚本执行异常：项目一",
        "[ERR] 脚本执行异常：项目二",
        "[ERR] 脚本执行异常：项目三",
        "[ERR] 脚本执行异常：项目四",
        "[ERR] 脚本执行异常：项目五",
        "[ERR] 脚本执行异常：项目六",
        "[ERR] 脚本执行异常：项目七",
        "[ERR] 脚本执行异常：项目八",
        "[ERR] 脚本执行异常：项目九",
        "[ERR] 脚本执行异常：项目十",
        "[ERR] 脚本执行异常：项目十一",
        "[ERR] 脚本执行异常：项目九",
        "[INF] 一条龙任务结束");
    Assert.Equal(
        string.Join(
            Environment.NewLine,
            "• 项目二",
            "• 项目一",
            "• 项目三",
            "• 项目四",
            "• 项目五",
            "• 项目六",
            "• 项目七",
            "• 项目八",
            "• 另有 3 项执行异常"),
        overflow.Message,
        "异常项应按首次出现顺序去重，且重复的第 9 项不得增加准确溢出数量");
}

static async Task CompletionFinalizationNoLogTimesOutAsync()
{
    var result = await RunCompletionFinalizationScenarioAsync(
        exitCode: null,
        noLogTimeout: TimeSpan.FromMilliseconds(80),
        hardTimeout: TimeSpan.FromSeconds(2),
        cancelAfterCompletion: false);

    Assert.Equal(RunState.TimedOut, result.State,
        "完成后进程迟迟不退出且无新日志时应保持无日志超时");
    Assert.True(result.Message.Contains("没有新日志", StringComparison.Ordinal),
        "无日志超时应保留原提示");
}

static async Task CompletionFinalizationHardTimeoutAsync()
{
    var result = await RunCompletionFinalizationScenarioAsync(
        exitCode: null,
        noLogTimeout: TimeSpan.FromSeconds(2),
        hardTimeout: TimeSpan.FromMilliseconds(80),
        cancelAfterCompletion: false);

    Assert.Equal(RunState.TimedOut, result.State,
        "完成后进程迟迟不退出时应保持最长运行超时");
    Assert.True(result.Message.Contains("运行超过", StringComparison.Ordinal),
        "最长运行超时应保留原提示");
}

static async Task CompletionFinalizationCancellationIsPreservedAsync()
{
    var cancelled = false;
    try
    {
        _ = await RunCompletionFinalizationScenarioAsync(
            exitCode: null,
            noLogTimeout: TimeSpan.FromSeconds(1),
            hardTimeout: TimeSpan.FromSeconds(2),
            cancelAfterCompletion: true);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }

    Assert.True(cancelled, "等待进程最终退出时取消必须继续传播取消语义");
}

static async Task HighVolumeLogKeepsBoundedExcerptAsync()
{
    var area = TestArea.Create();
    try
    {
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var task = monitor.MonitorCoreAsync(() => false, () => null, DateTimeOffset.Now,
            source, LogCheckpoint.Empty,
            line => new LogObservation(RunCompleted: line.Contains("tasks-completed")),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), null, CancellationToken.None);

        var content = new StringBuilder();
        for (var index = 0; index < 20_000; index++)
        {
            content.Append("verbose-line-").Append(index).Append('-').Append('x', 80).AppendLine();
        }
        content.AppendLine("kind: tasks-completed");
        await File.WriteAllTextAsync(area.File("burst.log"), content.ToString());

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RunState.Succeeded, result.State, "高频日志中的完成标记未识别");
        Assert.True(result.LogExcerpt is { Count: <= 20 }, "日志摘录超过固定上限");
        Assert.True(result.LogExcerpt!.All(line => line.Length <= 600), "日志摘录单行超过固定上限");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task LongLogLineDetectsMarkerAcrossChunksAsync()
{
    var area = TestArea.Create();
    try
    {
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var task = monitor.MonitorCoreAsync(() => false, () => null, DateTimeOffset.Now,
            source, LogCheckpoint.Empty,
            line => new LogObservation(RunCompleted: line.Contains("tasks-completed")),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), null, CancellationToken.None);

        var longLine = new string('x', 65_532) + "tasks-completed" + new string('y', 1_000);
        await File.WriteAllTextAsync(area.File("long.log"), longLine + Environment.NewLine);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RunState.Succeeded, result.State, "跨读取块的完成标记未识别");
        Assert.True(result.LogExcerpt is { Count: <= 20 }, "超长日志导致摘录无界增长");
        Assert.True(result.LogExcerpt!.All(line => line.Length <= 600), "超长日志摘录未截断");
    }
    finally
    {
        area.Dispose();
    }
}

static async Task Utf8CharacterSplitAcrossPollsRemainsIntactAsync()
{
    using var area = TestArea.Create();
    var targetPath = area.File("a-target.log");
    var probePath = area.File("z-probe.log");
    const string expectedLine = "中文日志 kind: tasks-completed";
    const string probeLine = "probe-ready";
    var targetBytes = Encoding.UTF8.GetBytes(expectedLine + Environment.NewLine);
    var firstCharacterBytes = Encoding.UTF8.GetByteCount("中");
    var splitIndex = firstCharacterBytes - 1;

    await File.WriteAllBytesAsync(targetPath, targetBytes[..splitIndex]);
    await File.WriteAllTextAsync(probePath, probeLine + Environment.NewLine, new UTF8Encoding(false));

    var receivedLines = new ConcurrentQueue<string>();
    var probeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
    var monitorTask = monitor.MonitorCoreAsync(
        () => false,
        () => null,
        DateTimeOffset.Now,
        new LogSource(area.Root, "*.log"),
        LogCheckpoint.Empty,
        line => new LogObservation(RunCompleted: line.Contains("tasks-completed", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        new DirectProgress<string>(line =>
        {
            receivedLines.Enqueue(line);
            if (string.Equals(line, probeLine, StringComparison.Ordinal))
            {
                probeObserved.TrySetResult();
            }
        }),
        CancellationToken.None);

    await probeObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await using (var appendStream = new FileStream(
                     targetPath,
                     FileMode.Append,
                     FileAccess.Write,
                     FileShare.ReadWrite | FileShare.Delete))
    {
        await appendStream.WriteAsync(targetBytes.AsMemory(splitIndex));
        await appendStream.FlushAsync();
    }

    var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal(RunState.Succeeded, result.State, "跨轮询完成标记应继续被识别");
    Assert.True(receivedLines.Contains(expectedLine),
        $"跨轮询 UTF-8 中文行应保持完整，实际：{string.Join(" | ", receivedLines)}");
}

static async Task Utf8BomAndCarryRemainCompatibleAsync()
{
    using var area = TestArea.Create();
    var targetPath = area.File("a-target.log");
    var probePath = area.File("z-probe.log");
    const string initialTail = "ascii 中文";
    const string expectedCarryLine = "ascii 中文 carry-line";
    const string probeLine = "probe-ready";
    var initialBytes = Encoding.UTF8.GetPreamble()
        .Concat(Encoding.UTF8.GetBytes(initialTail))
        .ToArray();
    await File.WriteAllBytesAsync(targetPath, initialBytes);
    await File.WriteAllTextAsync(probePath, probeLine + Environment.NewLine, new UTF8Encoding(false));

    var receivedLines = new ConcurrentQueue<string>();
    var probeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
    var monitorTask = monitor.MonitorCoreAsync(
        () => false,
        () => null,
        DateTimeOffset.Now,
        new LogSource(area.Root, "*.log"),
        LogCheckpoint.Empty,
        line => new LogObservation(RunCompleted: line.Contains("tasks-completed", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        new DirectProgress<string>(line =>
        {
            receivedLines.Enqueue(line);
            if (string.Equals(line, probeLine, StringComparison.Ordinal))
            {
                probeObserved.TrySetResult();
            }
        }),
        CancellationToken.None);

    await probeObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await using (var appendStream = new FileStream(
                     targetPath,
                     FileMode.Append,
                     FileAccess.Write,
                     FileShare.ReadWrite | FileShare.Delete))
    {
        var appendedBytes = Encoding.UTF8.GetBytes(
            " carry-line" + Environment.NewLine + "kind: tasks-completed" + Environment.NewLine);
        await appendStream.WriteAsync(appendedBytes);
        await appendStream.FlushAsync();
    }

    var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal(RunState.Succeeded, result.State, "BOM 日志中的完成标记应继续被识别");
    Assert.True(receivedLines.Contains(expectedCarryLine),
        $"BOM 与无换行尾部应形成完整 carry 行，实际：{string.Join(" | ", receivedLines)}");
}

static async Task Utf8DecodeStateResetsAfterTruncationAsync()
{
    using var area = TestArea.Create();
    var targetPath = area.File("a-target.log");
    var probePath = area.File("z-probe.log");
    const string expectedLine = "kind: tasks-completed";
    const string probeLine = "probe-ready";
    await File.WriteAllTextAsync(targetPath, new string('x', 1_000), new UTF8Encoding(false));
    await File.WriteAllTextAsync(probePath, probeLine + Environment.NewLine, new UTF8Encoding(false));

    var receivedLines = new ConcurrentQueue<string>();
    var probeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
    var monitorTask = monitor.MonitorCoreAsync(
        () => false,
        () => null,
        DateTimeOffset.Now,
        new LogSource(area.Root, "*.log"),
        LogCheckpoint.Empty,
        line => new LogObservation(RunCompleted: string.Equals(line, expectedLine, StringComparison.Ordinal)),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        new DirectProgress<string>(line =>
        {
            receivedLines.Enqueue(line);
            if (string.Equals(line, probeLine, StringComparison.Ordinal))
            {
                probeObserved.TrySetResult();
            }
        }),
        CancellationToken.None);

    await probeObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var replacementBytes = Encoding.UTF8.GetPreamble()
        .Concat(Encoding.UTF8.GetBytes(expectedLine + Environment.NewLine))
        .ToArray();
    await using (var replacementStream = new FileStream(
                     targetPath,
                     FileMode.Create,
                     FileAccess.Write,
                     FileShare.None))
    {
        await replacementStream.WriteAsync(replacementBytes);
        await replacementStream.FlushAsync();
    }

    var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal(RunState.Succeeded, result.State, "截断后的完成标记应继续被识别");
    Assert.True(receivedLines.Contains(expectedLine),
        $"截断后应清除旧 carry 和 decoder 状态，实际：{string.Join(" | ", receivedLines)}");
}

static async Task ReplacedLogReadsNewPrefixWhenLengthReachesOldOffsetAsync()
{
    using var area = TestArea.Create();
    var targetPath = area.File("a-target.log");
    var replacementPath = area.File("replacement.tmp");
    var probePath = area.File("z-probe.log");
    const string completionLine = "kind: tasks-completed";
    const string probeLine = "probe-ready";
    var oldContent = "old-prefix-" + new string('x', 256) + Environment.NewLine;
    await File.WriteAllTextAsync(targetPath, oldContent, new UTF8Encoding(false));
    await File.WriteAllTextAsync(probePath, probeLine + Environment.NewLine, new UTF8Encoding(false));

    var probeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
    var monitorTask = monitor.MonitorCoreAsync(
        () => false,
        () => null,
        DateTimeOffset.Now,
        new LogSource(area.Root, "*.log"),
        LogCheckpoint.Empty,
        line => new LogObservation(RunCompleted: string.Equals(line, completionLine, StringComparison.Ordinal)),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromSeconds(2),
        new DirectProgress<string>(line =>
        {
            if (string.Equals(line, probeLine, StringComparison.Ordinal))
            {
                probeObserved.TrySetResult();
            }
        }),
        CancellationToken.None);

    await probeObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var replacementContent = completionLine + Environment.NewLine + new string('n', oldContent.Length);
    await File.WriteAllTextAsync(replacementPath, replacementContent, new UTF8Encoding(false));
    File.Move(replacementPath, targetPath, overwrite: true);

    var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(3));
    Assert.Equal(RunState.Succeeded, result.State,
        "固定路径替换为不同且更长的文件后必须从新文件前缀读取完成标记");
}

static async Task RegrownLogReadsNewPrefixWhenLengthReachesOldOffsetAsync()
{
    using var area = TestArea.Create();
    var targetPath = area.File("a-target.log");
    var probePath = area.File("z-probe.log");
    const string completionLine = "kind: tasks-completed";
    const string probeLine = "probe-ready";
    var oldContent = "old-prefix-" + new string('x', 256) + Environment.NewLine;
    await File.WriteAllTextAsync(targetPath, oldContent, new UTF8Encoding(false));
    await File.WriteAllTextAsync(probePath, probeLine + Environment.NewLine, new UTF8Encoding(false));

    var probeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
    var monitorTask = monitor.MonitorCoreAsync(
        () => false,
        () => null,
        DateTimeOffset.Now,
        new LogSource(area.Root, "*.log"),
        LogCheckpoint.Empty,
        line => new LogObservation(RunCompleted: string.Equals(line, completionLine, StringComparison.Ordinal)),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromSeconds(2),
        new DirectProgress<string>(line =>
        {
            if (string.Equals(line, probeLine, StringComparison.Ordinal))
            {
                probeObserved.TrySetResult();
            }
        }),
        CancellationToken.None);

    await probeObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var replacementContent = completionLine + Environment.NewLine + new string('n', oldContent.Length);
    using (var rewriteStream = new FileStream(
               targetPath,
               FileMode.Open,
               FileAccess.Write,
               FileShare.None))
    {
        rewriteStream.SetLength(0);
        rewriteStream.Write(Encoding.UTF8.GetBytes(replacementContent));
        rewriteStream.Flush(flushToDisk: true);
    }

    var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(3));
    Assert.Equal(RunState.Succeeded, result.State,
        "同一日志清空后在下次扫描前回长，必须从新文件前缀读取完成标记");
}

static async Task PersistentLogReadFailureIsReportedAtNoLogTimeoutAsync()
{
    using var area = TestArea.Create();
    var logPath = area.File("locked.log");
    await File.WriteAllTextAsync(logPath, "existing log" + Environment.NewLine);
    await using var exclusiveLock = new FileStream(
        logPath,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.None);

    var result = await new LogMonitor(TimeSpan.FromMilliseconds(10)).MonitorCoreAsync(
        () => false,
        () => null,
        DateTimeOffset.Now,
        new LogSource(area.Root, "*.log"),
        LogCheckpoint.Empty,
        _ => default,
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromSeconds(2),
        null,
        CancellationToken.None);

    Assert.Equal(RunState.TimedOut, result.State, "日志读取失败仍应保持原超时状态");
    Assert.True(result.Message.Contains("日志读取失败", StringComparison.Ordinal),
        $"持续读取错误应替代普通无日志提示，实际：{result.Message}");
}

static async Task TransientLogReadFailureClearsAfterRecoveryAsync()
{
    using var area = TestArea.Create();
    var logPath = area.File("a-locked.log");
    var probePath = area.File("z-probe.log");
    const string probeLine = "probe-ready";
    await File.WriteAllTextAsync(logPath, "existing log" + Environment.NewLine);
    await File.WriteAllTextAsync(probePath, probeLine + Environment.NewLine);
    using var exclusiveLock = new FileStream(
        logPath,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.None);

    var probeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var monitorTask = new LogMonitor(TimeSpan.FromMilliseconds(10)).MonitorCoreAsync(
        () => false,
        () => null,
        DateTimeOffset.Now,
        new LogSource(area.Root, "*.log"),
        LogCheckpoint.Empty,
        line => new LogObservation(
            RunCompleted: line.Contains("tasks-completed", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        new DirectProgress<string>(line =>
        {
            if (string.Equals(line, probeLine, StringComparison.Ordinal))
            {
                probeObserved.TrySetResult();
            }
        }),
        CancellationToken.None);

    await probeObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    exclusiveLock.Dispose();
    await File.AppendAllTextAsync(logPath, "kind: tasks-completed" + Environment.NewLine);

    var result = await monitorTask.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.Equal(RunState.Succeeded, result.State, "短暂读取错误恢复后应继续识别完成标记");
    Assert.False(result.Message.Contains("日志读取失败", StringComparison.Ordinal),
        $"恢复读取后旧错误不应污染成功结果，实际：{result.Message}");
}

static async Task LogScanCancellationPrecedesSameBatchCompletionAsync()
{
    using var area = TestArea.Create();
    using var cancellation = new CancellationTokenSource();
    const string cancelLine = "cancel-now";
    const string completionLine = "kind: tasks-completed";
    await File.WriteAllLinesAsync(area.File("runtime.log"), [cancelLine, completionLine]);

    RunResult? returnedResult = null;
    var cancellationObserved = false;
    try
    {
        returnedResult = await new LogMonitor(TimeSpan.FromMilliseconds(10)).MonitorCoreAsync(
            () => false,
            () => null,
            DateTimeOffset.Now,
            new LogSource(area.Root, "*.log"),
            LogCheckpoint.Empty,
            line =>
            {
                if (string.Equals(line, cancelLine, StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                }

                return new LogObservation(
                    RunCompleted: string.Equals(line, completionLine, StringComparison.Ordinal));
            },
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            null,
            cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        cancellationObserved = true;
    }

    Assert.True(cancellationObserved,
        $"首行取消后必须立即传播取消，不能返回同批后续终态；实际：{returnedResult?.State}");
}

static async Task LogProgressDoesNotCaptureSynchronizationContextAsync()
{
    var previousContext = SynchronizationContext.Current;
    var countingContext = new CountingSynchronizationContext();
    var receivedLines = 0;
    var queue = new AutomationQueueService();
    queue.LogReceived += (_, _) => receivedLines++;

    try
    {
        SynchronizationContext.SetSynchronizationContext(countingContext);
        var runTask = queue.RunAsync(
            [new BurstLogAdapter(50_000)],
            Workflow((ToolId.BetterGi, 1)),
            new AppSettings());
        SynchronizationContext.SetSynchronizationContext(previousContext);
        await runTask;
    }
    finally
    {
        SynchronizationContext.SetSynchronizationContext(previousContext);
    }

    Assert.Equal(50_000, receivedLines, "高频日志未同步送达有界缓冲入口");
    Assert.Equal(0, countingContext.PostCount, "日志错误捕获了 UI 同步上下文");
}

static async Task NoLogTimesOutAsync()
{
    var area = TestArea.Create();
    try
    {
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var result = await monitor.MonitorCoreAsync(() => false, () => null, DateTimeOffset.Now,
            new LogSource(area.Root, "*.log"), LogCheckpoint.Empty, _ => default,
            TimeSpan.FromMilliseconds(70), TimeSpan.FromSeconds(2), null, CancellationToken.None);
        Assert.Equal(RunState.TimedOut, result.State, "无日志应进入超时状态");
    }
    finally
    {
        area.Dispose();
    }
}

static Task MaaEndEnabledSelfExitTaskOmitsQuitAfterRunAsync()
{
    var arguments = BuildMaaEndArguments("""
        {
          "instances": [
            {
              "name": "中文 日常",
              "controllerName": "Win32-Front",
              "tasks": [
                {
                  "taskName": "__MXU_KILLPROC__",
                  "enabled": true,
                  "enabledByController": {
                    "Win32-Front": true
                  },
                  "optionValues": {
                    "__MXU_KILLPROC_SELF_OPTION__": {
                      "value": true
                    }
                  }
                }
              ]
            }
          ]
        }
        """, "中文 日常");

    Assert.SequenceEqual(new[] { "--autostart", "--instance", "中文 日常" },
        arguments, "已配置适用的自身退出任务时不应再传入 --quit-after-run");
    return Task.CompletedTask;
}

static Task MaaEndDisabledSelfExitTaskOmitsQuitAfterRunAsync()
{
    var disabledTaskArguments = BuildMaaEndArguments("""
        {
          "instances": [
            {
              "name": "当前实例",
              "controllerName": "Win32-Front",
              "tasks": [
                {
                  "taskName": "__MXU_KILLPROC__",
                  "enabled": false,
                  "enabledByController": {
                    "Win32-Front": true
                  },
                  "optionValues": {
                    "__MXU_KILLPROC_SELF_OPTION__": {
                      "value": true
                    }
                  }
                }
              ]
            }
          ]
        }
        """);
    var disabledOptionArguments = BuildMaaEndArguments("""
        {
          "instances": [
            {
              "name": "当前实例",
              "controllerName": "Win32-Front",
              "tasks": [
                {
                  "taskName": "__MXU_KILLPROC__",
                  "enabled": true,
                  "enabledByController": {
                    "Win32-Front": true
                  },
                  "optionValues": {
                    "__MXU_KILLPROC_SELF_OPTION__": {
                      "value": false
                    }
                  }
                }
              ]
            }
          ]
        }
        """);

    var expected = new[] { "--autostart", "--instance", "当前实例" };
    Assert.SequenceEqual(expected, disabledTaskArguments, "禁用的自身退出任务不应触发参数注入");
    Assert.SequenceEqual(expected, disabledOptionArguments, "自身退出选项关闭时不应触发参数注入");
    return Task.CompletedTask;
}

static Task MaaEndControllerMismatchOmitsQuitAfterRunAsync()
{
    var arguments = BuildMaaEndArguments("""
        {
          "instances": [
            {
              "name": "当前实例",
              "controllerName": "Win32-Front",
              "tasks": [
                {
                  "taskName": "__MXU_KILLPROC__",
                  "enabled": true,
                  "enabledByController": {
                    "Win32-Front": false,
                    "ADB": true
                  },
                  "optionValues": {
                    "__MXU_KILLPROC_SELF_OPTION__": {
                      "value": true
                    }
                  }
                }
              ]
            }
          ]
        }
        """);

    Assert.SequenceEqual(new[] { "--autostart", "--instance", "当前实例" },
        arguments, "任务不适用当前控制器时不应触发参数注入");
    return Task.CompletedTask;
}

static Task MaaEndOtherInstanceSelfExitTaskOmitsQuitAfterRunAsync()
{
    var arguments = BuildMaaEndArguments("""
        {
          "instances": [
            {
              "name": "当前实例",
              "controllerName": "Win32-Front",
              "tasks": []
            },
            {
              "name": "其他实例",
              "controllerName": "Win32-Front",
              "tasks": [
                {
                  "taskName": "__MXU_KILLPROC__",
                  "enabled": true,
                  "enabledByController": {
                    "Win32-Front": true
                  },
                  "optionValues": {
                    "__MXU_KILLPROC_SELF_OPTION__": {
                      "value": true
                    }
                  }
                }
              ]
            }
          ]
        }
        """);

    Assert.SequenceEqual(new[] { "--autostart", "--instance", "当前实例" },
        arguments, "其他实例的自身退出任务不应向当前实例注入参数");
    return Task.CompletedTask;
}

static Task MaaEndUncertainSelfExitConfigurationOmitsQuitAfterRunAsync()
{
    var missingConfigArguments = BuildMaaEndArguments(null);
    var invalidConfigArguments = BuildMaaEndArguments("{");
    var unexpectedRootArguments = BuildMaaEndArguments("[]");
    var incompleteConfigArguments = BuildMaaEndArguments("""
        {
          "instances": [
            {
              "name": "当前实例",
              "controllerName": "Win32-Front",
              "tasks": [
                {
                  "taskName": "__MXU_KILLPROC__",
                  "enabled": true,
                  "optionValues": {}
                }
              ]
            }
          ]
        }
        """);

    var expected = new[] { "--autostart", "--instance", "当前实例" };
    Assert.SequenceEqual(expected, missingConfigArguments, "配置缺失时不应注入退出参数");
    Assert.SequenceEqual(expected, invalidConfigArguments, "配置 JSON 异常时不应注入退出参数");
    Assert.SequenceEqual(expected, unexpectedRootArguments, "配置结构异常时不应注入退出参数");
    Assert.SequenceEqual(expected, incompleteConfigArguments, "配置字段不完整时不应注入退出参数");
    return Task.CompletedTask;
}

static IReadOnlyList<string> BuildMaaEndArguments(string? configJson, string instanceName = "当前实例")
{
    var area = TestArea.Create();
    var configDirectory = Path.Combine(area.Root, "config");
    var configPath = area.File(Path.Combine("config", "mxu-MaaEnd.json"));

    try
    {
        if (configJson is not null)
        {
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(configPath, configJson);
        }

        var settings = new AppSettings
        {
            MaaEndPath = area.File("MaaEnd fixture.exe"),
            MaaEndInstance = instanceName
        };
        return new MaaEndAdapter().BuildStartInfo(settings).ArgumentList.ToArray();
    }
    finally
    {
        if (File.Exists(configPath))
        {
            File.Delete(configPath);
        }
        if (Directory.Exists(configDirectory))
        {
            Directory.Delete(configDirectory, recursive: false);
        }
        area.Dispose();
    }
}

static async Task ProfileDiscoveryDistinguishesSuccessfulCandidatesAndEmptyAsync()
{
    using var area = TestArea.Create();
    var executablePath = area.File("tool.exe");

    var oneDragonDirectory = area.File(Path.Combine("User", "OneDragon"));
    Directory.CreateDirectory(oneDragonDirectory);
    await File.WriteAllTextAsync(Path.Combine(oneDragonDirectory, "Beta.json"), "{}");
    await File.WriteAllTextAsync(Path.Combine(oneDragonDirectory, "Alpha.json"), "{}");
    await File.WriteAllTextAsync(Path.Combine(oneDragonDirectory, "ignored.txt"), "ignored");
    Assert.SequenceEqual(
        ["Alpha", "Beta"],
        AssertDiscoverySuccess(
            ToolDiscoveryService.DiscoverBetterGiProfiles(executablePath, "OneDragon"),
            "BetterGI 有配置"),
        "BetterGI 应返回排序后的 JSON 配置名");

    var scriptGroupDirectory = area.File(Path.Combine("User", "ScriptGroup"));
    Directory.CreateDirectory(scriptGroupDirectory);
    Assert.Equal(
        0,
        AssertDiscoverySuccess(
            ToolDiscoveryService.DiscoverBetterGiProfiles(executablePath, "ScriptGroups"),
            "BetterGI 空配置目录").Count,
        "存在且可读的 BetterGI 空目录应返成功为空");

    var configDirectory = area.File("config");
    Directory.CreateDirectory(configDirectory);
    var maaConfigPath = Path.Combine(configDirectory, "gui.new.json");
    await File.WriteAllTextAsync(maaConfigPath, """
        {"Configurations":{"Beta":{},"Alpha":{}}}
        """);
    Assert.SequenceEqual(
        ["Alpha", "Beta"],
        AssertDiscoverySuccess(
            ToolDiscoveryService.DiscoverMaaProfiles(executablePath),
            "MAA 有配置"),
        "MAA 应返回排序后的配置名");
    await File.WriteAllTextAsync(maaConfigPath, """{"Configurations":{}}""");
    Assert.Equal(
        0,
        AssertDiscoverySuccess(
            ToolDiscoveryService.DiscoverMaaProfiles(executablePath),
            "MAA 空配置").Count,
        "存在且结构正确的 MAA 空配置应返成功为空");

    var maaEndConfigPath = Path.Combine(configDirectory, "mxu-MaaEnd.json");
    await File.WriteAllTextAsync(maaEndConfigPath, """
        {"instances":[{"name":"日常"},{"name":"周常"}]}
        """);
    Assert.SequenceEqual(
        ["日常", "周常"],
        AssertDiscoverySuccess(
            ToolDiscoveryService.DiscoverMaaEndInstances(executablePath),
            "MaaEnd 有实例"),
        "MaaEnd 应返回有效实例名");
    await File.WriteAllTextAsync(maaEndConfigPath, """{"instances":[]}""");
    Assert.Equal(
        0,
        AssertDiscoverySuccess(
            ToolDiscoveryService.DiscoverMaaEndInstances(executablePath),
            "MaaEnd 空实例").Count,
        "存在且结构正确的 MaaEnd 空实例应返成功为空");
}

static async Task ProfileDiscoveryReportsMissingAndUnreadableStorageAsync()
{
    using var missingArea = TestArea.Create();
    var missingExecutable = missingArea.File("tool.exe");
    AssertDiscoveryFailure(
        ToolDiscoveryService.DiscoverBetterGiProfiles(missingExecutable, "OneDragon"),
        "BetterGI 配置目录缺失");
    AssertDiscoveryFailure(
        ToolDiscoveryService.DiscoverMaaProfiles(missingExecutable),
        "MAA 配置文件缺失");
    AssertDiscoveryFailure(
        ToolDiscoveryService.DiscoverMaaEndInstances(missingExecutable),
        "MaaEnd 配置文件缺失");

    using var lockedArea = TestArea.Create();
    var configDirectory = lockedArea.File("config");
    Directory.CreateDirectory(configDirectory);
    var configPath = Path.Combine(configDirectory, "gui.new.json");
    await File.WriteAllTextAsync(configPath, """{"Configurations":{}}""");
    await using var locked = new FileStream(
        configPath,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.None);
    AssertDiscoveryFailure(
        ToolDiscoveryService.DiscoverMaaProfiles(lockedArea.File("MAA.exe")),
        "MAA 配置文件锁定");

    var maaEndConfigPath = Path.Combine(configDirectory, "mxu-MaaEnd.json");
    await File.WriteAllTextAsync(maaEndConfigPath, """{"instances":[]}""");
    await using var maaEndLocked = new FileStream(
        maaEndConfigPath,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.None);
    AssertDiscoveryFailure(
        ToolDiscoveryService.DiscoverMaaEndInstances(lockedArea.File("MaaEnd.exe")),
        "MaaEnd 配置文件锁定");

    using var inaccessibleArea = TestArea.Create();
    var inaccessibleConfigDirectory = inaccessibleArea.File("config");
    Directory.CreateDirectory(Path.Combine(inaccessibleConfigDirectory, "gui.new.json"));
    AssertDiscoveryFailure(
        ToolDiscoveryService.DiscoverMaaProfiles(inaccessibleArea.File("MAA.exe")),
        "MAA 配置路径不可读");
}

static async Task ProfileDiscoveryRejectsInvalidJsonStructuresAsync()
{
    (string Name, string FileName, string Json, Func<string, DiscoveryOutcome> Discover)[] cases =
    [
        ("MAA 损坏 JSON", "gui.new.json", "{", ToolDiscoveryService.DiscoverMaaProfiles),
        ("MAA 顶层数组", "gui.new.json", "[]", ToolDiscoveryService.DiscoverMaaProfiles),
        ("MAA 嵌套配置非对象", "gui.new.json",
            """{"Configurations":{"Default":[]}}""", ToolDiscoveryService.DiscoverMaaProfiles),
        ("MaaEnd 损坏 JSON", "mxu-MaaEnd.json", "{", ToolDiscoveryService.DiscoverMaaEndInstances),
        ("MaaEnd 顶层数组", "mxu-MaaEnd.json", "[]", ToolDiscoveryService.DiscoverMaaEndInstances),
        ("MaaEnd 数组元素非对象", "mxu-MaaEnd.json",
            """{"instances":[1]}""", ToolDiscoveryService.DiscoverMaaEndInstances)
    ];

    foreach (var testCase in cases)
    {
        using var area = TestArea.Create();
        var configDirectory = area.File("config");
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, testCase.FileName), testCase.Json);

        AssertDiscoveryFailure(testCase.Discover(area.File("tool.exe")), testCase.Name);
    }
}

static async Task AdapterValidationRejectsInvalidProfileConfigurationAsync()
{
    using var maaArea = TestArea.Create();
    Directory.CreateDirectory(maaArea.File("config"));
    Directory.CreateDirectory(maaArea.File("debug"));
    var maaExecutable = maaArea.File("maa-invalid-profile.exe");
    await File.WriteAllTextAsync(maaExecutable, string.Empty);
    var maaConfigPath = maaArea.File(Path.Combine("config", "gui.new.json"));
    foreach (var invalidJson in new[]
             {
                 "{",
                 "[]",
                 """{"Configurations":{"Default":[]}}"""
             })
    {
        await File.WriteAllTextAsync(maaConfigPath, invalidJson);
        var validation = new MaaAdapter().Validate(new AppSettings
        {
            MaaPath = maaExecutable,
            MaaProfile = "Default"
        });
        Assert.False(validation.IsValid, "MAA 错误配置结构应返验证失败");
        Assert.True(validation.Issues.Any(issue => issue.Contains("MAA 配置", StringComparison.Ordinal)),
            "MAA 错误配置结构应返简短配置错误");
    }

    using var maaEndArea = TestArea.Create();
    Directory.CreateDirectory(maaEndArea.File("config"));
    Directory.CreateDirectory(maaEndArea.File("debug"));
    var maaEndExecutable = maaEndArea.File("maaend-invalid-instance.exe");
    await File.WriteAllTextAsync(maaEndExecutable, string.Empty);
    foreach (var invalidJson in new[]
             {
                 "{",
                 """{"instances":[1]}"""
             })
    {
        await File.WriteAllTextAsync(
            maaEndArea.File(Path.Combine("config", "mxu-MaaEnd.json")),
            invalidJson);
        var maaEndValidation = new MaaEndAdapter().Validate(new AppSettings
        {
            MaaEndPath = maaEndExecutable,
            MaaEndInstance = "当前实例"
        });
        Assert.False(maaEndValidation.IsValid, "MaaEnd 错误实例结构应返验证失败");
        Assert.True(maaEndValidation.Issues.Any(issue => issue.Contains("MaaEnd 配置", StringComparison.Ordinal)),
            "MaaEnd 错误实例结构应返简短配置错误");
    }
}

static IReadOnlyList<string> AssertDiscoverySuccess(DiscoveryOutcome outcome, string scenario)
{
    if (outcome is not DiscoveryOutcome.Success success)
    {
        throw new InvalidOperationException($"{scenario}应返发现成功，实际：{outcome}");
    }

    return success.Candidates;
}

static void AssertDiscoveryFailure(DiscoveryOutcome outcome, string scenario)
{
    if (outcome is not DiscoveryOutcome.Failure failure)
    {
        throw new InvalidOperationException($"{scenario}应返发现失败，实际：{outcome}");
    }

    Assert.True(!string.IsNullOrWhiteSpace(failure.Error), $"{scenario}失败应包含简短错误");
}

static Task ArgumentsPreserveChineseAndSpacesAsync()
{
    var settings = new AppSettings
    {
        BetterGiPath = @"D:\Games With Spaces\BetterGI.exe",
        BetterGiMode = "OneDragon",
        BetterGiProfile = "中文 配置",
        MaaPath = @"D:\Games With Spaces\MAA.exe",
        MaaProfile = "Default Profile",
        MaaEndPath = @"D:\Games With Spaces\MaaEnd.exe",
        MaaEndInstance = "快速 日常"
    };
    Assert.SequenceEqual(new[] { "--startOneDragon", "中文 配置" },
        new BetterGiAdapter().BuildStartInfo(settings).ArgumentList, "BetterGI 参数拆分错误");
    Assert.SequenceEqual(new[] { "--config", "Default Profile" },
        new MaaAdapter().BuildStartInfo(settings).ArgumentList, "MAA 参数拆分错误");
    Assert.SequenceEqual(new[] { "--autostart", "--instance", "快速 日常" },
        new MaaEndAdapter().BuildStartInfo(settings).ArgumentList, "MaaEnd 参数拆分错误");
    return Task.CompletedTask;
}

static Task MissingPathIsRejectedAsync()
{
    var settings = new AppSettings { BetterGiPath = @"Z:\not-existing\BetterGI.exe" };
    var result = new BetterGiAdapter().Validate(settings);
    Assert.False(result.IsValid, "不存在的程序路径不应通过验证");
    Assert.True(result.Issues.Any(issue => issue.Contains("找不到", StringComparison.Ordinal)), "应给出找不到程序的提示");
    return Task.CompletedTask;
}

static Task RunningProcessIsRejectedAsync()
{
    var currentExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("无法取得测试进程路径");
    var settings = new AppSettings { MaaPath = currentExecutable };
    var result = new MaaAdapter().Validate(settings);
    Assert.True(result.Issues.Any(issue => issue.Contains("已经在运行", StringComparison.Ordinal)), "应识别同名运行中进程");
    return Task.CompletedTask;
}

static Task MaaPostActionsAreUserConfigurableAsync()
{
    var area = TestArea.Create();
    var configDirectory = Path.Combine(area.Root, "config");
    var debugDirectory = Path.Combine(area.Root, "debug");
    var configPath = area.File(Path.Combine("config", "gui.new.json"));
    try
    {
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(debugDirectory);
        var executablePath = area.File("maa-validation-fixture.exe");
        File.WriteAllText(executablePath, string.Empty);
        File.WriteAllText(configPath, """
            {
              "Configurations": {
                "Default": {
                  "Gui": {
                    "StartUpSettings": {
                      "RunDirectly": true
                    }
                  }
                }
              }
            }
            """);

        var result = new MaaAdapter().Validate(new AppSettings
        {
            MaaPath = executablePath,
            MaaProfile = "Default"
        });

        Assert.True(result.IsValid, $"MAA 未配置 ExitSelf 时不应阻止启动：{string.Join("；", result.Issues)}");
        return Task.CompletedTask;
    }
    finally
    {
        if (File.Exists(configPath))
        {
            File.Delete(configPath);
        }
        if (Directory.Exists(configDirectory))
        {
            Directory.Delete(configDirectory, recursive: false);
        }
        if (Directory.Exists(debugDirectory))
        {
            Directory.Delete(debugDirectory, recursive: false);
        }
        area.Dispose();
    }
}

static async Task SettingsAndHistoryRoundTripAsync()
{
    var area = TestArea.Create();
    try
    {
        var settingsStore = new SettingsStore(area.Root);
        var expected = new AppSettings { MaaEndInstance = "全套日常", NoLogTimeoutMinutes = 17 };
        await settingsStore.SaveAsync(expected);
        var actual = (await settingsStore.LoadAsync()).Settings;
        Assert.Equal("全套日常", actual.MaaEndInstance, "设置未恢复");
        Assert.Equal(17, actual.NoLogTimeoutMinutes, "超时设置未恢复");

        var history = new HistoryStore(area.Root);
        await history.AppendAsync(new RunRecord
        {
            ToolId = ToolId.Maa,
            ToolName = "MAA",
            StartedAt = DateTimeOffset.Now.AddMinutes(-1),
            EndedAt = DateTimeOffset.Now,
            State = RunState.Succeeded,
            Message = "完成"
        });
        var records = await history.ReadAllAsync();
        Assert.Equal(1, records.Count, "历史记录数量不正确");
        Assert.Equal(RunState.Succeeded, records[0].State, "历史状态未恢复");
    }
    finally
    {
        area.Dispose();
    }
}

static Task ToolCatalogCreatesAllAdaptersAsync()
{
    var adapters = ToolCatalog.CreateAdapters();
    Assert.SequenceEqual(Enum.GetValues<ToolId>(), adapters.Select(adapter => adapter.Id), "工具目录顺序或适配器缺失");
    Assert.True(ToolCatalog.All.All(definition => !string.IsNullOrWhiteSpace(definition.Name)
        && !string.IsNullOrWhiteSpace(definition.GameName)
        && !string.IsNullOrWhiteSpace(definition.DisplayName)
        && !string.IsNullOrWhiteSpace(definition.FallbackGlyph)
        && !string.IsNullOrWhiteSpace(definition.FallbackBackground)
        && !string.IsNullOrWhiteSpace(definition.FallbackForeground)), "工具目录缺少展示元数据");
    return Task.CompletedTask;
}

static WorkflowTaskSetting[] Workflow(params (ToolId Id, int Channel)[] tasks) => tasks
    .Select(task => new WorkflowTaskSetting { ToolId = task.Id, IsEnabled = true, Channel = task.Channel })
    .ToArray();

static async Task<RunResult> RunAdapterLogScenarioAsync(
    Func<LogMonitor, ProcessAutomationAdapter> createAdapter,
    int exitCode,
    params string[] lines) =>
    await RunAdapterLogScenarioWithSettingsAsync(createAdapter, new AppSettings(), exitCode, lines);

static async Task<RunResult> RunAdapterLogScenarioWithSettingsAsync(
    Func<LogMonitor, ProcessAutomationAdapter> createAdapter,
    AppSettings settings,
    int exitCode,
    params string[] lines)
{
    var area = TestArea.Create();
    try
    {
        var log = area.File("runtime.log");
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log", IncludeSubdirectories: true, FollowRotatedFiles: true);
        var checkpoint = monitor.Capture(source);
        await File.AppendAllLinesAsync(log, lines);
        using var handle = new AutomationRunHandle(
            await StartExitedProcessAsync(exitCode),
            DateTimeOffset.Now,
            source,
            checkpoint);
        return await createAdapter(monitor).MonitorAsync(
            handle,
            settings,
            null,
            CancellationToken.None);
    }
    finally
    {
        area.Dispose();
    }
}

static async Task<Process> StartExitedProcessAsync(int exitCode)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("/d");
    startInfo.ArgumentList.Add("/c");
    startInfo.ArgumentList.Add("exit");
    startInfo.ArgumentList.Add(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
    var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("无法启动离线退出码测试进程。");
    await process.WaitForExitAsync();
    return process;
}

static async Task<RunResult> RunCompletionFinalizationScenarioAsync(
    int? exitCode,
    TimeSpan noLogTimeout,
    TimeSpan hardTimeout,
    bool cancelAfterCompletion,
    params string[] linesAfterCompletion)
{
    var area = TestArea.Create();
    var log = area.File("runtime.log");
    using var process = StartControllableProcess();
    using var cancellation = new CancellationTokenSource();
    try
    {
        await File.WriteAllTextAsync(log, "旧运行日志" + Environment.NewLine);
        var monitor = new LogMonitor(TimeSpan.FromMilliseconds(10));
        var source = new LogSource(area.Root, "*.log");
        var completionObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trailingLineObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitorTask = monitor.MonitorAsync(
            process,
            DateTimeOffset.Now,
            source,
            monitor.Capture(source),
            line => new LogObservation(
                RunCompleted: line.Contains("run-completed", StringComparison.Ordinal),
                InternalError: line.Contains("internal-error", StringComparison.Ordinal),
                BlockingFailure: line.Contains("blocking-failure", StringComparison.Ordinal)),
            noLogTimeout,
            hardTimeout,
            new DirectProgress<string>(line =>
            {
                if (line.Contains("run-completed", StringComparison.Ordinal))
                {
                    completionObserved.TrySetResult();
                }

                if (linesAfterCompletion.Length > 0
                    && line.Contains(linesAfterCompletion[^1], StringComparison.Ordinal))
                {
                    trailingLineObserved.TrySetResult();
                }
            }),
            cancellation.Token,
            CompletionFinalizationPolicy.RequireProcessExit);

        await File.AppendAllTextAsync(log, "run-completed" + Environment.NewLine);
        await completionObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        if (linesAfterCompletion.Length > 0)
        {
            await File.AppendAllLinesAsync(log, linesAfterCompletion);
            await trailingLineObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        if (cancelAfterCompletion)
        {
            cancellation.Cancel();
        }
        else if (exitCode is not null)
        {
            await ExitControllableProcessAsync(process, exitCode.Value);
        }

        return await monitorTask.WaitAsync(TimeSpan.FromSeconds(2));
    }
    finally
    {
        await EnsureControllableProcessExitedAsync(process);
        area.Dispose();
    }
}

static Process StartControllableProcess()
{
    var startInfo = new ProcessStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("/d");
    startInfo.ArgumentList.Add("/q");
    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("无法启动离线可控进程。");
}

static async Task ExitControllableProcessAsync(Process process, int exitCode)
{
    if (process.HasExited)
    {
        return;
    }

    await process.StandardInput.WriteLineAsync(
        $"exit /b {exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    process.StandardInput.Close();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
}

static async Task EnsureControllableProcessExitedAsync(Process process)
{
    if (process.HasExited)
    {
        return;
    }

    try
    {
        await ExitControllableProcessAsync(process, 0);
    }
    catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}

static TaskCompletionSource NewGate() =>
    new(TaskCreationOptions.RunContinuationsAsynchronously);

static byte[] CreateMaaResourceArchive(
    string version,
    bool includeVersion = true,
    string? versionJsonOverride = null,
    string? unsafeEntry = null,
    bool unsafeEntryIsSymlink = false) =>
    MaaResourceTestData.CreateArchive(
        version,
        includeVersion,
        versionJsonOverride,
        unsafeEntry,
        unsafeEntryIsSymlink);

file sealed class CountingSynchronizationContext : SynchronizationContext
{
    private int _postCount;

    public int PostCount => Volatile.Read(ref _postCount);

    public override void Post(SendOrPostCallback callback, object? state) =>
        Interlocked.Increment(ref _postCount);
}

file sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

file sealed class BurstLogAdapter(int lineCount) : IAutomationAdapter
{
    public ToolId Id => ToolId.BetterGi;

    public string DisplayName => "突发日志测试";

    public ValidationResult Validate(AppSettings settings) => ValidationResult.Success();

    public bool IsProcessRunning(AppSettings settings) => false;

    public ProcessStartInfo BuildStartInfo(AppSettings settings) => new();

    public Task<AutomationRunHandle> StartAsync(AppSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult(new AutomationRunHandle(
            Process.GetCurrentProcess(),
            DateTimeOffset.Now,
            new LogSource(Path.GetTempPath(), "GachaOps-none"),
            LogCheckpoint.Empty));

    public Task<RunResult> MonitorAsync(
        AutomationRunHandle handle,
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < lineCount; index++)
        {
            progress?.Report($"burst-{index}");
        }

        return Task.FromResult(new RunResult(RunState.Succeeded, "完成"));
    }
}

file sealed class FakeAdapter : IAutomationAdapter
{
    private readonly Queue<RunState> _outcomes;
    private int _startCount;

    public FakeAdapter(ToolId id, params RunState[] outcomes)
    {
        Id = id;
        _outcomes = new Queue<RunState>(outcomes);
    }

    public ToolId Id { get; }

    public string DisplayName => ToolCatalog.Get(Id).DisplayName;

    public int StartCount => Volatile.Read(ref _startCount);

    public bool ProcessRunning { get; set; }

    public bool IsProcessRunning(AppSettings settings) => ProcessRunning;

    public Task? MonitorGate { get; init; }

    public Action? ValidateAction { get; init; }

    public Action? BeforeMonitorResult { get; init; }

    public Exception? StartException { get; init; }

    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValidationResult Validate(AppSettings settings)
    {
        ValidateAction?.Invoke();
        return ProcessRunning ? ValidationResult.Failure($"{DisplayName} 已经在运行") : ValidationResult.Success();
    }

    public ProcessStartInfo BuildStartInfo(AppSettings settings) => new();

    public Task<AutomationRunHandle> StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);
        Started.TrySetResult();
        if (StartException is not null)
        {
            throw StartException;
        }

        return Task.FromResult(new AutomationRunHandle(Process.GetCurrentProcess(), DateTimeOffset.Now,
            new LogSource(Path.GetTempPath(), "GachaOps-none"), LogCheckpoint.Empty));
    }

    public async Task<RunResult> MonitorAsync(AutomationRunHandle handle, AppSettings settings,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (MonitorGate is not null)
        {
            await MonitorGate.WaitAsync(cancellationToken);
        }

        BeforeMonitorResult?.Invoke();
        var outcome = _outcomes.Dequeue();
        return new RunResult(outcome, outcome == RunState.Succeeded ? "完成" : "失败");
    }
}

file sealed class PreflightAdapter(ToolId id) : IAutomationAdapter
{
    private int _validationCount;

    public ToolId Id { get; } = id;

    public string DisplayName => ToolCatalog.Get(Id).DisplayName;

    public int ValidationCount => Volatile.Read(ref _validationCount);

    public bool IsProcessRunning(AppSettings settings) => false;

    public Func<int, ValidationResult>? ValidationHandler { get; init; }

    public ValidationResult Validate(AppSettings settings)
    {
        var count = Interlocked.Increment(ref _validationCount);
        return ValidationHandler?.Invoke(count) ?? ValidationResult.Success();
    }

    public ProcessStartInfo BuildStartInfo(AppSettings settings) => new();

    public Task<AutomationRunHandle> StartAsync(AppSettings settings, CancellationToken cancellationToken) =>
        throw new NotSupportedException("预检测试不会启动工具。");

    public Task<RunResult> MonitorAsync(
        AutomationRunHandle handle,
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("预检测试不会监控工具。");
}

file sealed class FakeToolUpdateProvider : IToolUpdateProvider
{
    private readonly bool _updateAvailable;
    private int _checkCount;
    private int _updateCount;
    private int _recoverCount;
    private int _validationCount;

    public FakeToolUpdateProvider(ToolId id, bool updateAvailable = false)
    {
        Id = id;
        _updateAvailable = updateAvailable;
    }

    public ToolId Id { get; }

    public string DisplayName => ToolCatalog.Get(Id).DisplayName;

    public int CheckCount => Volatile.Read(ref _checkCount);

    public int UpdateCount => Volatile.Read(ref _updateCount);

    public int RecoverCount => Volatile.Read(ref _recoverCount);

    public int ValidationCount => Volatile.Read(ref _validationCount);

    public TaskCompletionSource CheckStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource UpdateStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Func<CancellationToken, Task<ToolUpdateCheckResult>>? CheckHandler { get; init; }

    public Func<ToolUpdateExecutionContext, CancellationToken, Task<ToolUpdateExecutionResult>>?
        UpdateHandler
    { get; init; }

    public Func<ToolUpdatePendingState, CancellationToken, Task<ToolUpdateRecoveryResult>>?
        RecoverHandler
    { get; init; }

    public ValidationResult Validation { get; init; } = ValidationResult.Success();

    public ValidationResult ValidateUpdate(AppSettings settings)
    {
        Interlocked.Increment(ref _validationCount);
        return Validation;
    }

    public Func<CancellationToken, Task<ToolVersionFingerprint>>? InstallationHandler { get; init; }

    public Task<ToolVersionFingerprint> CaptureInstallationFingerprintAsync(
        AppSettings settings, CancellationToken cancellationToken) =>
        InstallationHandler?.Invoke(cancellationToken) ?? Task.FromResult(Fingerprint("1.0.0"));

    public Task<ToolUpdateCheckResult> CheckAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _checkCount);
        CheckStarted.TrySetResult();
        return CheckHandler?.Invoke(cancellationToken)
            ?? Task.FromResult(Check(Id, _updateAvailable));
    }

    public Task<ToolUpdateExecutionResult> UpdateAsync(
        AppSettings settings,
        ToolUpdateCheckResult check,
        ToolUpdateExecutionContext context,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _updateCount);
        UpdateStarted.TrySetResult();
        return UpdateHandler?.Invoke(context, cancellationToken)
            ?? Task.FromResult(Success(Id, check.TargetVersion));
    }

    public Task<ToolUpdateRecoveryResult> RecoverAsync(
        AppSettings settings,
        ToolUpdatePendingState pending,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _recoverCount);
        return RecoverHandler?.Invoke(pending, cancellationToken)
            ?? Task.FromResult(ToolUpdateRecoveryResult.RetryAllowed("允许重新检查"));
    }

    public static ToolUpdateCheckResult Check(ToolId id, bool updateAvailable) =>
        new ToolUpdateCheckResult(
            id,
            Fingerprint("1.0.0"),
            updateAvailable ? "2.0.0" : "1.0.0",
            updateAvailable)
        {
            UpdateItems = updateAvailable
                ? [ToolCatalog.Get(id).Name]
                : Array.Empty<string>()
        };

    public static ToolUpdateExecutionResult Success(ToolId id, string version) =>
        ToolUpdateExecutionResult.Success(
            $"{ToolCatalog.Get(id).DisplayName} 更新完成",
            Fingerprint(version),
            [ToolCatalog.Get(id).Name]);

    public static ToolVersionFingerprint Fingerprint(string version) =>
        new(version, $"HASH-{version}", version.Length, DateTimeOffset.UtcNow);
}

file sealed class FakeMaaProgramUpdateOperations : IMaaProgramUpdateOperations
{
    private readonly List<string>? _order;
    private int _programCheckCount;
    private int _updateCount;
    private int _recoveryCount;

    public FakeMaaProgramUpdateOperations(List<string>? order = null)
    {
        _order = order;
    }

    public ToolUpdateCheckResult ProgramCheck { get; init; } = new(
        ToolId.Maa,
        FakeToolUpdateProvider.Fingerprint("1.0.0"),
        "1.0.0",
        false);

    public bool ProgramUpdated { get; private set; }

    public int ProgramCheckCount => Volatile.Read(ref _programCheckCount);

    public int UpdateCount => Volatile.Read(ref _updateCount);

    public int RecoveryCount => Volatile.Read(ref _recoveryCount);

    public ToolUpdateRecoveryResult RecoveryResult { get; init; } =
        ToolUpdateRecoveryResult.RetryAllowed("测试恢复允许重试");

    public Task<ToolUpdateCheckResult> CheckAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _programCheckCount);
        _order?.Add("program-check");
        return Task.FromResult(ProgramCheck);
    }

    public Task<ToolUpdateExecutionResult> UpdateAsync(
        AppSettings settings,
        ToolUpdateCheckResult check,
        ToolUpdateExecutionContext context,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _updateCount);
        _order?.Add("program-update");
        ProgramUpdated = true;
        return Task.FromResult(ToolUpdateExecutionResult.Success(
            "MAA 程序更新完成",
            FakeToolUpdateProvider.Fingerprint(check.TargetVersion)));
    }

    public Task<ToolUpdateRecoveryResult> RecoverAsync(
        AppSettings settings,
        ToolUpdatePendingState pending,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _recoveryCount);
        return Task.FromResult(RecoveryResult);
    }

    public Task<ToolVersionFingerprint> CaptureFingerprintAsync(
        AppSettings settings,
        CancellationToken cancellationToken) =>
        Task.FromResult(ProgramUpdated
            ? FakeToolUpdateProvider.Fingerprint(ProgramCheck.TargetVersion)
            : ProgramCheck.CurrentFingerprint);
}

file sealed class DelayedReleaseBodyHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new DelayedReleaseBodyStream())
        });
}

file sealed class DelayedReleaseBodyStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
}

file sealed class StaticGitHubReleaseClient(string version = "1.0.0") : IGitHubReleaseClient
{
    public Task<string> GetLatestVersionAsync(
        string repository,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(version);
}

file sealed class VersionTestUpdateProvider(
    string currentVersion,
    string latestVersion,
    string executablePath)
    : ToolUpdateProviderBase(new StaticGitHubReleaseClient(latestVersion))
{
    public override ToolId Id => ToolId.BetterGi;

    public override string DisplayName => "版本测试工具";

    protected override string Repository => "test/version-provider";

    protected override string GetExecutablePath(AppSettings settings) => executablePath;

    protected override string ReadVersion(AppSettings settings) => currentVersion;

    protected override IReadOnlyList<string> GetFingerprintPaths(AppSettings settings) => [executablePath];

    public override ProcessStartInfo BuildUpdateStartInfo(AppSettings settings) => new(executablePath);
}

file sealed class FakeMaaResourceUpdateModule : IMaaResourceUpdateModule
{
    private readonly List<string>? _order;
    private int _checkCount;
    private int _scopeCaptureCount;
    private int _recoveryCount;

    public FakeMaaResourceUpdateModule(List<string>? order = null)
    {
        _order = order;
    }

    public Queue<MaaResourceUpdatePlan> CheckPlans { get; init; } = [];

    public string ScopeFingerprint { get; set; } = "RESOURCE";

    public Func<bool>? ProgramUpdated { get; set; }

    public List<MaaResourceUpdatePlan> AppliedPlans { get; } = [];

    public int CheckCount => Volatile.Read(ref _checkCount);

    public int ScopeCaptureCount => Volatile.Read(ref _scopeCaptureCount);

    public int RecoveryCount => Volatile.Read(ref _recoveryCount);

    public ValidationResult Validate(string maaExecutablePath) => ValidationResult.Success();

    public Task<string> CaptureLocalFingerprintAsync(
        string maaExecutablePath,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _scopeCaptureCount);
        return Task.FromResult(ScopeFingerprint);
    }

    public Task<MaaResourceUpdatePlan> CheckAsync(
        string maaExecutablePath,
        CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _checkCount);
        if (count > 1 && ProgramUpdated is not null && !ProgramUpdated())
        {
            throw new InvalidOperationException("程序更新完成前不得重新检查资源");
        }

        _order?.Add("resource-check");
        return Task.FromResult(CheckPlans.Count > 0
            ? CheckPlans.Dequeue()
            : new MaaResourceUpdatePlan(
                new string('a', 40),
                "2026-08-25 18:05:48.000",
                "2026-08-25 18:05:48.000",
                false));
    }

    public Task<MaaResourceUpdateResult> UpdateAsync(
        string maaExecutablePath,
        MaaResourceUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        _order?.Add("resource-update");
        AppliedPlans.Add(plan);
        return Task.FromResult(MaaResourceUpdateResult.Success("MAA 资源更新完成"));
    }

    public Task<MaaResourceRecoveryResult> RecoverAsync(
        string maaExecutablePath,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _recoveryCount);
        return Task.FromResult(MaaResourceRecoveryResult.NoAction());
    }
}

file static class MaaResourceTestData
{
    public const string OldVersion = "2026-08-01 00:00:00.000";
    public const string NewVersion = "2026-08-25 18:05:48.000";
    public const string NewerVersion = "2026-08-26 18:05:48.000";

    public static byte[] CreateArchive(
        string version,
        bool includeVersion = true,
        string? versionJsonOverride = null,
        string? unsafeEntry = null,
        bool unsafeEntryIsSymlink = false)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["battle_data.json"] = "new-battle_data.json",
            ["infrast.json"] = "new-infrast.json",
            ["item_index.json"] = "new-item_index.json",
            ["recruitment.json"] = "new-recruitment.json",
            ["stages.json"] = "new-stages.json",
            ["template/existing.txt"] = "new-existing",
            ["template/new.txt"] = "new-file"
        };
        if (includeVersion)
        {
            files["version.json"] = versionJsonOverride ?? JsonSerializer.Serialize(new
            {
                activity = new { name = "测试活动", time = 1_787_342_400 },
                last_updated = version
            });
        }

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "MaaResource-main/README.md", "official archive");
            foreach (var (relativePath, content) in files)
            {
                WriteEntry(archive, $"MaaResource-main/resource/{relativePath}", content);
            }

            if (unsafeEntry is not null)
            {
                var entry = WriteEntry(archive, unsafeEntry, "unsafe");
                if (unsafeEntryIsSymlink)
                {
                    entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                }
            }
        }

        return stream.ToArray();
    }

    private static ZipArchiveEntry WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
        return entry;
    }
}

file sealed class MaaResourceFixture : IDisposable
{
    private MaaResourceFixture(
        string installationRoot,
        string appDataRoot,
        MaaResourceTestHttpHandler handler,
        HttpClient client,
        MaaResourceUpdateModule module)
    {
        InstallationRoot = installationRoot;
        AppDataRoot = appDataRoot;
        Handler = handler;
        Client = client;
        Module = module;
    }

    public string InstallationRoot { get; }

    public string MaaPath => Path.Combine(InstallationRoot, "MAA.exe");

    public string ResourceRoot => Path.Combine(InstallationRoot, "resource");

    public string AppDataRoot { get; }

    public string TargetVersion => MaaResourceTestData.NewVersion;

    public MaaResourceTestHttpHandler Handler { get; }

    public HttpClient Client { get; }

    public MaaResourceUpdateModule Module { get; }

    public static MaaResourceFixture Create(
        TestArea area,
        string installationName = "MAA 安装 空格",
        string? appDataRoot = null,
        Action<MaaResourceUpdateFaultPoint>? faultInjector = null)
    {
        var installationRoot = Path.Combine(area.Root, installationName);
        var resourceRoot = Path.Combine(installationRoot, "resource");
        Directory.CreateDirectory(Path.Combine(resourceRoot, "template"));
        Directory.CreateDirectory(Path.Combine(resourceRoot, "custom"));
        File.WriteAllBytes(Path.Combine(installationRoot, "MAA.exe"), [0x4D, 0x5A, 0x01, 0x02]);
        foreach (var name in new[]
                 {
                     "battle_data.json",
                     "infrast.json",
                     "item_index.json",
                     "recruitment.json",
                     "stages.json"
                 })
        {
            File.WriteAllText(Path.Combine(resourceRoot, name), $"old-{name}");
        }

        File.WriteAllText(
            Path.Combine(resourceRoot, "version.json"),
            JsonSerializer.Serialize(new { last_updated = MaaResourceTestData.OldVersion }));
        File.WriteAllText(Path.Combine(resourceRoot, "template", "existing.txt"), "old-existing");
        File.WriteAllText(Path.Combine(resourceRoot, "custom", "user.json"), "custom-user-data");

        var handler = new MaaResourceTestHttpHandler { BranchCommit = new string('a', 40) };
        handler.AddRelease(
            new string('a', 40),
            MaaResourceTestData.NewVersion,
            MaaResourceTestData.CreateArchive(MaaResourceTestData.NewVersion));
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var resolvedAppDataRoot = appDataRoot ?? Path.Combine(area.Root, $"app-data-{installationName}");
        var module = new MaaResourceUpdateModule(client, resolvedAppDataRoot, faultInjector);
        return new MaaResourceFixture(
            installationRoot,
            resolvedAppDataRoot,
            handler,
            client,
            module);
    }

    public void AddRemoteRelease(string commit, string version)
    {
        Handler.AddRelease(commit, version, MaaResourceTestData.CreateArchive(version));
    }

    public void Dispose() => Client.Dispose();
}

file sealed class MaaResourceTestHttpHandler : HttpMessageHandler
{
    public string BranchCommit { get; set; } = new('a', 40);

    public Dictionary<string, string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, byte[]> Archives { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<string> RequestedUris { get; } = [];

    public int FailBranchRequests { get; set; }

    public int FailDownloadRequests { get; set; }

    public bool BlockDownloads { get; set; }

    public int BranchCount { get; private set; }

    public int DownloadCount { get; private set; }

    public TaskCompletionSource DownloadStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseDownload { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void AddRelease(string commit, string version, byte[] archive)
    {
        Versions[commit] = version;
        Archives[commit] = archive;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri
            ?? throw new InvalidOperationException("测试请求缺少 URI");
        RequestedUris.Enqueue(uri.AbsoluteUri);
        if (uri.AbsolutePath.EndsWith("/branches/main", StringComparison.Ordinal))
        {
            BranchCount++;
            if (FailBranchRequests > 0)
            {
                FailBranchRequests--;
                throw new HttpRequestException("transient branch failure");
            }

            return JsonResponse(JsonSerializer.Serialize(new
            {
                name = "main",
                commit = new { sha = BranchCommit }
            }));
        }

        if (uri.AbsolutePath.EndsWith("/contents/resource/version.json", StringComparison.Ordinal))
        {
            var commit = GetQueryValue(uri, "ref");
            return Versions.TryGetValue(commit, out var version)
                ? JsonResponse(JsonSerializer.Serialize(new { last_updated = version }))
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (uri.AbsolutePath.Contains("/zipball/", StringComparison.Ordinal))
        {
            DownloadCount++;
            if (FailDownloadRequests > 0)
            {
                FailDownloadRequests--;
                throw new HttpRequestException("transient download failure");
            }

            DownloadStarted.TrySetResult();
            if (BlockDownloads)
            {
                await ReleaseDownload.Task.WaitAsync(cancellationToken);
            }

            var commit = uri.Segments[^1].Trim('/');
            if (!Archives.TryGetValue(commit, out var archive))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            };
            response.Content.Headers.ContentType = new("application/zip");
            return response;
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string GetQueryValue(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return string.Empty;
    }
}

file sealed class TestArea : IDisposable
{
    private TestArea(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    public static TestArea Create() => new(Path.Combine(Path.GetTempPath(), "GachaOpsTests", Guid.NewGuid().ToString("N")));

    public string File(string name)
    {
        return Path.Combine(Root, name);
    }

    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            directories.Add(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                FileAttributes attributes;
                try
                {
                    attributes = System.IO.File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Atomic test writes may rename a temporary file between enumeration and inspection.
                    continue;
                }
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
                if (isDirectory && !isReparsePoint)
                {
                    pending.Push(entry);
                }
                else if (isDirectory)
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    for (var attempt = 1; attempt <= 5; attempt++)
                    {
                        try
                        {
                            System.IO.File.Delete(entry);
                            break;
                        }
                        catch (Exception exception) when (attempt < 5
                                                           && exception is IOException or UnauthorizedAccessException)
                        {
                            Thread.Sleep(25);
                        }
                    }
                }
            }
        }

        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: false);
                    break;
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(25);
                }
            }
        }
    }
}

file sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow.ToUniversalTime();
}

file sealed class FixedStackException(string message, string stackTrace) : Exception(message)
{
    public override string StackTrace => stackTrace;
}

file sealed class OwnershipTestUpdateProvider(string executablePath)
    : ToolUpdateProviderBase(new StaticGitHubReleaseClient("2.0.0"), TimeSpan.FromSeconds(10))
{
    public string CurrentVersion { get; set; } = "1.0.0";

    public string? AdditionalFingerprintPath { get; init; }

    public Action<int>? ReadVersionCallback { get; set; }

    public TaskCompletionSource<int> ProcessRecorded { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ToolId TestToolId { get; init; } = ToolId.BetterGi;

    public bool SelfUpdating { get; init; } = true;

    public bool WaitForUpdateProcessExit { get; init; }

    public string? UpdateWorkerProcessPath { get; init; }

    public int ReadVersionCount { get; private set; }

    public override ToolId Id => TestToolId;

    public override string DisplayName => "进程所有权测试工具";

    protected override string Repository => "test/process-ownership";

    protected override bool StartsSelfUpdatingApplication => SelfUpdating;

    protected override bool WaitForUpdateProcessExitBeforeCompletion => WaitForUpdateProcessExit;

    protected override IReadOnlyList<string> GetUpdateWorkerProcessPaths(AppSettings settings) =>
        UpdateWorkerProcessPath is null ? [] : [UpdateWorkerProcessPath];

    protected override string GetExecutablePath(AppSettings settings) => executablePath;

    protected override string ReadVersion(AppSettings settings)
    {
        ReadVersionCallback?.Invoke(++ReadVersionCount);
        return CurrentVersion;
    }

    protected override IReadOnlyList<string> GetFingerprintPaths(AppSettings settings) =>
        AdditionalFingerprintPath is null
            ? [executablePath]
            : [executablePath, AdditionalFingerprintPath];

    public Task<ToolVersionFingerprint> CaptureFingerprintForTestAsync() =>
        CaptureFingerprintAsync(new AppSettings(), CancellationToken.None);

    public override Task<ToolUpdateExecutionResult> UpdateAsync(
        AppSettings settings,
        ToolUpdateCheckResult check,
        ToolUpdateExecutionContext context,
        CancellationToken cancellationToken) =>
        base.UpdateAsync(settings, check, new ToolUpdateExecutionContext(
            async (processId, processPath, token) =>
            {
                // Signal only after the real coordinator has persisted the process identity.
                await context.ProcessStartedAsync(processId, processPath, token);
                ProcessRecorded.TrySetResult(processId);
            },
            context.ReportUpdateItemsAsync), cancellationToken);

    public override ProcessStartInfo BuildUpdateStartInfo(AppSettings settings)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--bf01-close-window-helper");
        return startInfo;
    }
}

file static class CloseWindowProcessHelper
{
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_TIMER = 0x0113;

    private static readonly WndProc s_wndProc = CustomWndProc;

    public static int Run()
    {
        var className = $"GachaOps_CloseWindowHelper_{Guid.NewGuid():N}";
        var wndClass = new WNDCLASS
        {
            lpfnWndProc = s_wndProc,
            lpszClassName = className
        };

        if (RegisterClass(ref wndClass) == 0)
        {
            return 1;
        }

        var hwnd = CreateWindowEx(
            WS_EX_TOOLWINDOW,
            className,
            "GachaOps BF-01",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            -32000,
            -32000,
            100,
            100,
            nint.Zero,
            nint.Zero,
            nint.Zero,
            nint.Zero);

        if (hwnd == nint.Zero)
        {
            return 1;
        }

        _ = SetTimer(hwnd, 1, 60000, nint.Zero);

        MSG msg;
        while (GetMessage(out msg, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        return (int)msg.wParam;
    }

    private static nint CustomWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_CLOSE:
            case WM_TIMER:
                DestroyWindow(hWnd);
                return nint.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return nint.Zero;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass([In] ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int X,
        int Y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetTimer(nint hWnd, nint nIDEvent, uint uElapse, nint lpTimerFunc);
}

file sealed class NotificationTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

file sealed class NotificationSlowContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
}

file static class Assert
{
    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}。期望：{expected}；实际：{actual}");
        }
    }

    public static void False(bool value, string message)
    {
        if (value)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"{message}。期望：[{string.Join(", ", expected)}]；实际：[{string.Join(", ", actual)}]");
        }
    }
}
