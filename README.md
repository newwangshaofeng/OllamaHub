# OllamaHub

OllamaHub 是一个本地 HTTP 代理服务，对外模拟部分 Ollama API，并可按模型声明的协议能力直通或转换转发到上游服务，方便在 Visual Studio Copilot Chat 的 BYOM 场景中把本地 Ollama 入口桥接到其他模型提供商。

当前版本目标：
- 对外兼容 Visual Studio Copilot Chat BYOM 常用的 Ollama 接口
- 配置文件格式尽量兼容 [oai-compatible-copilot](https://github.com/JohnnyZ93/oai-compatible-copilot)
- 当前已支持按 `apiMode` 选择 OpenAI / Anthropic / Ollama 兼容入口

## 已支持接口

- `GET /`
- `GET /api/version`
- `GET /api/tags`
- `GET /api/ps`
- `POST /api/show`
- `POST /api/chat`
- `POST /v1/chat/completions`

其中：
- `/api/tags` 用于返回可用模型列表
- `/api/show` 用于返回模型详情
- `/api/chat` 支持非流式和流式聊天转发
- `/v1/chat/completions` 提供 OpenAI Chat Completions 兼容入口

## 配置文件

配置文件名固定为：

- `settings.json`

配置文件位置：

- 与可执行文件同级目录

例如发布后为：

- `OllamaHub.exe`
- `settings.json`

## 配置格式
配置结构尽量兼容 `oai-compatible-copilot` 的 `providers` / `models` 风格。一般格式如下：

```json
{
  "host": "127.0.0.1",
  "port": 11434,
  "logging": 
  {
    "level": "None"
  },
  "providers": [
    {
      "id": "360智脑",
      "baseUrl": "https://api.360.cn/",
      "apiKey": "你的API KEY"
    }
  ],
  "models": [
    {
      "id": "anthropic/claude-sonnet-4-5",
      "owned_by": "360智脑",
      "displayName": "Claude Sonnet 4.5",
      "family": "claude",
      "apiMode": "anthropic;openai",
      "context_length": 200000,
      "max_tokens": 8192,
      "vision": true,
      "temperature": 0,
      "top_p": 1,
      "extra": {
        "service_tier": "standard_only"
      }
    },
    {
      "id": "st/deepseek/deepseek-v4-pro",
      "owned_by": "360智脑",
      "displayName": "deepseek/deepseek-v4-pro",
      "family": "deepseek",
      "apiMode": "ollama;openai",
      "context_length": 1000000,
      "max_tokens": 384000,
      "temperature": 0,
      "top_p": 1
    },
    {
      "id": "z-ai/glm-5.1",
      "owned_by": "360智脑",
      "displayName": "z-ai/glm-5.1",
      "family": "glm",
      "apiMode": "ollama;openai",
      "context_length": 200000,
      "max_tokens": 128000,
      "temperature": 0,
      "top_p": 1
    }
  ]
}
```


当前实际使用到的字段：

### 根字段

- `host`: 可选，监听主机名或 IP，与 `port` 配合使用
- `port`: 可选，监听端口，与 `host` 配合使用
- `url`: 可选，单个监听地址，例如 `http://127.0.0.1:11434`
- `baseUrl`: 可选，全局默认上游 base URL
- `logging`: 可选，日志配置
- `providers`: 可选，供应商列表
- `models`: 必填，模型列表

监听地址解析优先级：

1. `url`
2. `host + port`

如果两者都不配置，则回退到 ASP.NET Core 默认监听方式。

### logging 字段

- `level`: 可选，日志输出等级，支持 `None`、`Error`、`Warning`、`Info`

等级规则：

- `None`: 不输出日志
- `Error`: 只输出错误日志
- `Warning`: 输出警告和错误日志
- `Info`: 输出所有日志

默认值：`None`

### provider 字段

- `id`: 供应商 ID
- `baseUrl`: 供应商基础地址
- `apiKey`: API Key
- `apiMode`: 可选，支持单个或多个协议模式，多个值用分号分隔，例如 `openai;anthropic`
- `headers`: 可选，自定义请求头

### model 字段

- `id`: 模型 ID；在 Anthropic 转换路径下同时作为 Anthropic 模型名
- `displayName`: 可选，显示名称
- `configId`: 可选，用于同模型多配置；暴露给 Ollama 时会显示为 `id::configId`
- `owned_by` / `provider` / `provide`: 提供商 ID，三者任选其一
- `family`: 可选，默认 `claude`
- `baseUrl`: 可选，覆盖 provider/baseUrl
- `apiKey`: 可选，覆盖 provider/apiKey
- `apiMode`: 可选，支持单个或多个协议模式，多个值用分号分隔，例如 `openai;anthropic`
- `context_length`: 可选，默认 `128000`
- `max_tokens`: 可选，默认 `4096`
- `vision`: 可选，默认 `false`，表示模型是否支持视觉能力；若为 `true`，`/api/show` 返回的 `capabilities` 中会包含 `vision`
- `temperature`: 可选
- `top_p`: 可选
- `headers`: 可选，模型级自定义请求头
- `extra`: 可选，在走 Anthropic 转换路径时会合并到 Anthropic 请求体

`apiMode` 选择规则：

- 若当前入口协议被模型声明支持，则优先同协议直通上游
- 若当前入口协议未被声明支持，但模型支持 `anthropic`，则回退到现有 Anthropic 转换链路
- 目前已识别的协议值为：`openai`、`anthropic`、`ollama`

## 示例配置

见仓库中的 `settings.json`。

核心示例：

- provider 定义 `anthropic` 的 `baseUrl`、`apiKey`、`apiMode`
- model 使用 `owned_by: "anthropic"`
- `apiMode` 支持单值或分号分隔多值
- `logging.level` 可控制日志输出等级，默认 `None`

## 启动方式

### 开发运行

在仓库根目录执行：

`dotnet run --project OllamaHub`

监听地址现在建议直接在 `settings.json` 中配置。

示例 1：使用 `host` + `port`

`"host": "127.0.0.1", "port": 11434`

示例 2：使用单个 `url`

`"url": "http://127.0.0.1:11434"`

当前优先推荐为 VS Copilot BYOM 配置：

`"url": "http://127.0.0.1:11434"`

如果你仍然需要，也可以继续使用 ASP.NET Core 自带环境变量覆盖，例如：

`set ASPNETCORE_URLS=http://127.0.0.1:11434`

然后再启动程序。

### 发布运行

示例：

`dotnet publish OllamaHub -c Release -o publish`

把 `settings.json` 放到 `publish` 目录，与生成的可执行文件同级。

## 在 Visual Studio Copilot Chat BYOM 中使用

思路是把 OllamaHub 当作本地 Ollama 服务。

建议步骤：

1. 启动 OllamaHub，并监听一个本地 HTTP 地址，例如 `http://127.0.0.1:11434`
2. 在 Visual Studio 的 Copilot Chat BYOM 中选择使用 Ollama / 本地 Ollama
3. 把地址指向 OllamaHub 的监听地址
4. Copilot 请求到达 OllamaHub 后，由它转发到配置中的 Anthropic 模型

## 请求映射说明

### OpenAI Chat Completions -> 上游

- 若模型支持 `openai`，则 `/v1/chat/completions` 会直接透传到上游 `/v1/chat/completions`
- 若模型不支持 `openai` 但支持 `anthropic`，则会按以下规则转换为 Anthropic

- `messages[].role = system` 会被合并为 Anthropic `system`
- `messages[].content` 文本会映射为 Anthropic `text`
- `messages[].content` 数组支持基础多模态转换：`text` 与 `image_url`
- `assistant.tool_calls` 会映射为 Anthropic `tool_use`
- `tool` 消息会映射为 Anthropic `tool_result`
- `tools` 会映射为 Anthropic `tools`
- `tool_choice` 会转换为 Anthropic `tool_choice`
- `temperature`、`top_p`、`max_tokens` 会映射到对应 Anthropic 字段
- 其他未显式处理的字段会按允许列表通过 `extra` 透传

### Ollama -> 上游

- 若模型支持 `ollama`，则 `/api/chat` 会直接透传到上游 `/api/chat`
- 若模型不支持 `ollama` 但支持 `anthropic`，则会按以下规则转换为 Anthropic

- `system` 消息会被合并为 Anthropic 的 `system`
- `user` / `assistant` 文本消息会映射到 Anthropic `messages`
- `assistant.tool_calls` 会映射为 Anthropic `tool_use`
- `tool` 角色消息会映射为 Anthropic `tool_result`
- `options.temperature` -> `temperature`
- `options.top_p` -> `top_p`
- `options.num_predict` -> `max_tokens`
- `extra` 会直接合并到 Anthropic 请求体

### Anthropic -> Ollama

- 非流式：把返回文本块合并为一个 Ollama chat 响应
- 流式：把 Anthropic SSE 事件转换为 Ollama 风格 NDJSON 分块输出

### Anthropic -> OpenAI Chat Completions

- 非流式：返回 OpenAI `chat.completion` JSON
- 流式：返回 OpenAI 风格 `text/event-stream`
- 工具调用会尽量映射为 `tool_calls`
- 若 Anthropic 返回 token 统计，会尽量映射到 OpenAI `usage`
- 流式工具调用会拆分为起始 metadata 块和 arguments 增量块，更贴近 OpenAI delta 语义
- 流结束前会额外输出一个包含 `usage` 的最终 chunk（若上游提供了 token 统计）

## 当前限制

当前版本只覆盖首批可用场景，存在以下限制：

- 仅对 `openai`、`anthropic`、`ollama` 三种协议值做内建处理
- 当前支持文本消息和基础工具调用透传
- 当前支持 `/v1/chat/completions` 的基础 OpenAI 兼容转发
- 尚未实现图像/多模态消息透传
- 仅实现了 Copilot Chat BYOM 所需的基础 Ollama 兼容面
- 模型元数据中的大小、量化等字段为代理占位值，不代表真实模型信息

## 测试

运行全部测试：

`dotnet test`

当前已覆盖：

- 配置加载与模型解析
- settings 中监听地址解析
- Ollama 请求到 Anthropic 请求的转换
- Ollama 工具调用到 Anthropic tool_use/tool_result 的转换
- Anthropic SSE 到 Ollama NDJSON 的映射
- OpenAI Chat Completions 到 Anthropic 的转换
- 分号分隔多 `apiMode` 的配置解析
- Anthropic 到 OpenAI Chat Completions 的非流式/流式映射
- Anthropic usage 到 OpenAI usage 的映射
- Anthropic 流式 tool_use 到 OpenAI tool_calls delta 的细粒度映射
- OpenAI 多模态 content 数组到 Anthropic text/image 的转换
- OpenAI 流式最终 usage chunk 输出

## 后续扩展方向

- OpenAI 兼容接口转发
- Gemini 转发
- 工具调用透传
- 多模态内容转发
- 更完整的 Ollama API 兼容
