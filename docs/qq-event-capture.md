# 官 Q 完整消息采集

启动时设置环境变量 `Rain__Diagnostics__CaptureQqMessages=true`，或配置 `Rain.Diagnostics.CaptureQqMessages` 为 `true`。默认关闭，修改后需重启。

日志写入运行目录 `Logs/qq-events/qq-<启动时间>-<会话标识>-<分卷>.jsonl`，每行是一个完整 JSON 记录，包含采集时间、机器人实例标识、事件类型及未经模型反序列化裁剪的完整 `data`。保留卡片未知字段和长文本，和 WebUI 日志的显示长度无关。

仅采集 `GROUP_MESSAGE_CREATE`、`GROUP_AT_MESSAGE_CREATE` 和 `C2C_MESSAGE_CREATE`；在消息去重前记录，因此可对照同一消息的全量与 @ 事件。不会记录鉴权帧、READY 或会话恢复事件。每条写入后关闭文件，约 32 MB 自动分卷，单条记录不截断。记录包含真实聊天内容和用户标识，仅供本次协议排查；`Logs/` 已排除出 Git。

停止采集时关闭上述开关并重启。已有日志保留，以便后续分析。
