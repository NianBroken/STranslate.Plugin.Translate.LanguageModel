# 语言模型翻译

`语言模型翻译` 是一个 STranslate 社区文本翻译插件。它通过兼容 OpenAI 格式的语言模型完成翻译，最终只返回模型生成的纯文本，不实现普通机器翻译，也不绑定任何单一供应商。

项目仓库：<https://github.com/NianBroken/STranslate.Plugin.Translate.LanguageModel>

## 功能

- 自定义 API 地址、API 密钥和模型 ID。
- 自定义 JSON 请求体与请求头。请求体保留合法的非消息字段，`messages`、`contents`、`input` 及提示词内容由插件统一生成，`model`、`stream` 和供应商扩展参数仍可由用户覆盖。
- `thinking` 按供应商协议从自定义请求体原样发送。以 MiniMax 为例，应填写在请求体中，例如 `{"thinking":{"type":"disabled"}}`，而不是请求头。
- 自定义请求体中的 `messages`、`contents`、`input` 以及提示词节点由插件生成，用户填写的这些重复字段会被忽略。其他字段按原始 JSON 类型递归合并，`model`、`stream`、`temperature` 和供应商扩展字段可以覆盖默认值。
- 自定义请求头中的 `Authorization`、`Accept` 和 `Content-Type` 由插件管理，其他合法请求头会按用户填写的类型转换为 HTTP 请求头值。
- 使用 STranslate 官方提示词模型和编辑窗口，支持 `$source`、`$target`、`$content` 变量。
- 默认启用流式输出，设置请求体中的 `stream` 为 `false` 可兼容非流式服务。
- 流式请求按相邻响应数据之间的空闲时间判断超时，只要模型持续返回数据，请求总时长可以超过设置的秒数。非流式请求仍按完整请求总时长判断超时。
- 自动重试，可自定义最大请求次数和超时时间。
- 兼容 OpenAI、GPT、Kimi、Qwen、MiniMax、Claude、Gemini 以及其他常见语言模型服务。
- 过滤思考过程、工具调用和其他中间信息，只显示纯文本译文。

## 配置

设置页按 API 地址、API 密钥、模型 ID、自定义请求体、自定义请求头、提示词配置、最大请求次数、超时时间和连接校验排列。除自定义请求体、自定义请求头和用户提示词外，其余设置必须填写。最大请求次数不得小于 1，超时时间不得小于 3 秒。

正式翻译会将 STranslate 传入的源语言、目标语言和原文分别替换到当前启用提示词中的 `$source`、`$target`、`$content`。插件不会另行创建独立的原文用户提示词。系统提示词必须由用户在官方提示词编辑窗口中填写。

连接校验使用独立内容，固定向模型发送空的 system 内容和用户消息“你好”，不会使用正式翻译提示词。API 地址、密钥、模型、请求体、请求头、重试次数和超时时间仍使用当前配置。

## 构建与安装

```powershell
dotnet build .\STranslate.Plugin.Translate.LanguageModel\STranslate.Plugin.Translate.LanguageModel.csproj --configuration Release --nologo
```

Release 安装包位于：

```text
.artifacts\Release\Plugins\STranslate.Plugin.Translate.LanguageModel\plugins\STranslate.Plugin.Translate.LanguageModel.spkg
```

在 STranslate 的插件设置页导入 `.spkg`，重启宿主后即可选择“语言模型翻译”。

正式版本也可以从 [GitHub Releases](https://github.com/NianBroken/STranslate.Plugin.Translate.LanguageModel/releases) 下载。插件配置页面最底部的“在 GitHub 上查看”按钮会使用系统默认浏览器打开项目仓库。

## 日志与隐私

插件通过 STranslate 官方 `Context.Logger` 记录请求时间、请求体 RAW、脱敏请求头、响应体 RAW、流式响应、重试过程和异常堆栈。API 密钥仅用于运行时请求，不写入源代码、默认配置、文档或安装包，日志中的认证值会被脱敏。

## 作者与许可证

- 作者：NianBroken
- 作者主页：<https://www.klaio.top/>
- 源代码：<https://github.com/NianBroken/STranslate.Plugin.Translate.LanguageModel>
- 许可证：[Apache License 2.0](LICENSE)
