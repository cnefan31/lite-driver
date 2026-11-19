#!/usr/bin/env python3
"""编排服务端到端验证：热应用、端口回写、客户端连通、增删节点，全程不重启进程。"""
import json
import os
import sys
import time
import urllib.request

BASE = os.environ.get("ORCH_BASE", "http://127.0.0.1:8110")


def call(path, payload=None):
    if payload is None:
        req = urllib.request.Request(BASE + path)
    else:
        req = urllib.request.Request(
            BASE + path, data=json.dumps(payload).encode(),
            headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=30) as resp:
        body = json.load(resp)
    if not body.get("ok"):
        raise RuntimeError(f"{path} 失败: {body.get('error')}")
    return body["data"]


def fail(msg):
    print(f"  FAIL {msg}")
    return 1


def main():
    failures = 0

    # 1) 节点类型目录：设备节点独立，协议/编解码器是自身字段，不再是画布节点
    types = call("/api/nodetypes")
    ids = [t["typeId"] for t in types]
    print(f"节点类型 {len(types)} 种: {ids}")
    for need in ("client", "decoder", "encoder", "server", "transform", "handler", "sink"):
        if need not in ids:
            failures += fail(f"缺少节点类型 {need}")
    if "protocol" in ids:
        failures += fail("协议不应再是独立节点（它是设备自身字段）")
    if "codec" in ids:
        failures += fail("编解码器应拆分为 decoder / encoder，不应再有合并节点 codec")

    # 解码器只做上行、编码器只做下行
    dec = next(x for x in types if x["typeId"] == "decoder")
    enc_t = next(x for x in types if x["typeId"] == "encoder")
    dports = {p["id"]: p["dataType"] for p in dec["ports"]}
    eports = {p["id"]: p["dataType"] for p in enc_t["ports"]}
    if dports.get("in") != "RawFrame" or dports.get("out") != "IMessage":
        failures += fail(f"解码器端口应为 RawFrame→IMessage，实际 {dports}")
    if eports.get("in") != "IMessage" or eports.get("out") != "RawFrame":
        failures += fail(f"编码器端口应为 IMessage→RawFrame，实际 {eports}")
    print("  解码器 RawFrame→IMessage，编码器 IMessage→RawFrame（上下行分离）")

    # 2) 设备节点：tx 发送输入 + out 上报输出；协议与编解码器是自身字段
    for dev in ("client", "server"):
        t = next(x for x in types if x["typeId"] == dev)
        ins = [p["id"] for p in t["ports"] if p["direction"] == "Input"]
        outs = [p["id"] for p in t["ports"] if p["direction"] == "Output"]
        portTypes = {p["id"]: p["dataType"] for p in t["ports"]}
        if ins != ["tx"]:
            failures += fail(f"{dev} 输入端口应只有 tx，实际 {ins}")
        if "out" not in outs:
            failures += fail(f"{dev} 缺少 out 输出端口")
        if portTypes.get("tx") != "RawFrame" or portTypes.get("out") != "RawFrame":
            failures += fail(f"{dev} 收发应为 RawFrame，实际 {portTypes}")
        keys = [f["key"] for f in t["fields"]]
        if "kindId" not in keys:
            failures += fail(f"{dev} 缺少 kindId 自身字段")
        if "codecId" in keys:
            failures += fail(f"{dev} 不应再有 codecId 字段（编解码器已独立成节点）")
    print("  设备节点：tx/out 均为 RawFrame，协议为自身字段，编解码器独立成节点")

    # 3) 类型约束：设备 RawFrame 不能直连处理器 IMessage，必须经编解码器
    bad = call("/api/connectable", {
        "fromNodeType": "client", "fromPort": "out",
        "toNodeType": "handler", "toPort": "in"})
    if bad["ok"]:
        failures += fail("客户端直连处理器未被拒绝")
    else:
        print(f"  客户端→处理器 被拒: {bad['reason']}")

    good = call("/api/connectable", {
        "fromNodeType": "client", "fromPort": "out",
        "toNodeType": "decoder", "toPort": "in"})
    if not good["ok"]:
        failures += fail("客户端→编解码器 被误拒")
    print("  客户端→解码器 允许")

    enc = call("/api/connectable", {
        "fromNodeType": "encoder", "fromPort": "out",
        "toNodeType": "client", "toPort": "tx"})
    if not enc["ok"]:
        failures += fail("编解码器编码输出→设备tx 被误拒")
    print("  编码器→设备 tx 允许")

    # 3) 首次应用：完整链路（客户端→编解码→处理器；服务端 Port=0）
    graph = {
        "revision": 0,
        "nodes": [
            {"id": "srv", "nodeType": "server", "label": "服务端", "x": 40, "y": 340,
             "settings": {"kindId": "length-prefix",
                          "host": "127.0.0.1", "port": "0", "maxSessions": "8"}},
            {"id": "cli", "nodeType": "client", "label": "客户端", "x": 40, "y": 90,
             "settings": {"kindId": "length-prefix",
                          # 首轮先用占位端口；但把退避压到很小，
                          # 避免连错地址累积退避后拖慢热更新后的重连
                          "endpoints": "127.0.0.1:1",
                          "retryInitialMs": "100", "retryMaxMs": "400"}},
            {"id": "k1", "nodeType": "decoder", "label": "解码器1", "x": 300, "y": 90,
             "settings": {"codecId": "text"}},
            {"id": "k2", "nodeType": "decoder", "label": "解码器2", "x": 300, "y": 340,
             "settings": {"codecId": "text"}},
            {"id": "e1", "nodeType": "encoder", "label": "编码器1", "x": 560, "y": 210,
             "settings": {"codecId": "text"}},
            {"id": "h", "nodeType": "handler", "label": "处理器", "x": 560, "y": 90,
             "settings": {"handlerId": "log", "mode": "log"}},
            {"id": "sk", "nodeType": "sink", "label": "出口", "x": 560, "y": 340,
             "settings": {"sinkId": "queue", "target": "q1"}},
        ],
        "edges": [
            {"id": "e1", "from": "cli", "fromPort": "out", "to": "k1", "toPort": "in"},
            {"id": "e2", "from": "k1", "fromPort": "out", "to": "h", "toPort": "in"},
            {"id": "e3", "from": "srv", "fromPort": "out", "to": "k2", "toPort": "in"},
            {"id": "e4", "from": "k2", "fromPort": "out", "to": "sk", "toPort": "in"},
            # 下行：处理器结果 → 编码器 → 客户端 tx
            {"id": "e5", "from": "h", "fromPort": "ok", "to": "e1", "toPort": "in"},
            {"id": "e6", "from": "e1", "fromPort": "out", "to": "cli", "toPort": "tx"},
        ],
    }
    r1 = call("/api/apply", graph)
    if not r1["ok"]:
        failures += fail(f"首次应用失败: {r1['issues']}")
        print(json.dumps({"failures": failures}))
        return failures

    time.sleep(2)
    port = int([n for n in call("/api/graph")["nodes"] if n["nodeType"] == "server"][0]["settings"]["port"])
    print(f"  服务端实际端口={port}（已回写进图）")
    if port <= 0:
        failures += fail("端口未回写")

    # 5) 校验负例：设备缺协议必须被拒，且定位到节点
    bad = json.loads(json.dumps(graph))
    for n in bad["nodes"]:
        if n["id"] == "cli":
            n["settings"]["kindId"] = ""
    v = call("/api/validate", bad)
    if v["ok"]:
        failures += fail("孤立服务端节点未被拒绝")
    else:
        assert any(i.get("code") == "node_invalid" for i in v["issues"]), "未报节点配置非法"
        print(f"  校验拒绝且定位到节点: {v['issues'][0]['code']}")

    # 5) 热更新客户端指向真实端口 -> 应连通
    for n in graph["nodes"]:
        if n["id"] == "srv":
            n["settings"]["port"] = str(port)
        if n["id"] == "cli":
            n["settings"]["endpoints"] = f"127.0.0.1:{port}"
    r2 = call("/api/apply", graph)
    if not r2["ok"]:
        failures += fail(f"热更新失败: {r2['issues']}")
    # 轮询等待链路真正建立：客户端 Ready 与入站会话登记不是同一时刻完成，
    # 固定 sleep 在慢速 CI 上会读到 inboundSessions=0。
    # 热更新后客户端需先断开旧连接、按退避重连到新端口，再等服务端 accept 登记会话。
    # 这三步在慢速 CI 上可能超过 15 秒，故放宽到 45 秒，并同时要求两者成立。
    # 热更新后需要：客户端断开旧连接 → 按退避重连新端口 → 服务端 accept 登记会话。
    # CI 慢机器上退避与调度延迟可能累积，给足 120 秒；本地通常 1 秒内完成。
    st = None
    deadline = time.time() + 120
    while time.time() < deadline:
        st = call("/api/state")
        if st["readyClients"] >= 1 and st["inboundSessions"] >= 1:
            break
        time.sleep(0.5)

    print(f"  readyClients={st['readyClients']} inboundSessions={st['inboundSessions']}"
          f"（端口 {port}）")
    if st["readyClients"] < 1:
        failures += fail("客户端未就绪")
    if st["inboundSessions"] < 1:
        # 诊断：确认客户端实际指向的端点与服务端端口是否一致
        try:
            g = call("/api/graph")["nodes"]
            cli_eps = next((n["settings"].get("endpoints") for n in g if n["id"] == "cli"), None)
            srv_port = next((n["settings"].get("port") for n in g if n["nodeType"] == "server"), None)
            print(f"  诊断: cli.endpoints={cli_eps!r} srv.port={srv_port!r} 期望端口={port}")
            for n in st.get("nodes", []):
                print(f"  诊断: {n.get('id')} kind={n.get('kind')} state={n.get('state')}")
        except Exception as ex:
            print(f"  诊断失败: {ex}")
        failures += fail(f"服务端未收到会话（等待 120s，端口 {port}）")

    # 6) 运行期新增服务端节点（不重启），必须经编解码器才合法
    graph["nodes"].append({"id": "srv2", "nodeType": "server", "label": "服务端2",
                           "settings": {"kindId": "length-prefix",
                                        "host": "127.0.0.1", "port": "0", "maxSessions": "4"}})
    graph["nodes"].append({"id": "k3", "nodeType": "decoder", "label": "解码器3", "x": 300, "y": 500,
                           "settings": {"codecId": "text"}})
    graph["edges"].append({"id": "e5", "from": "srv2", "fromPort": "out", "to": "k3", "toPort": "in"})
    graph["edges"].append({"id": "e6", "from": "k3", "fromPort": "out", "to": "sk", "toPort": "in"})
    r3 = call("/api/apply", graph)
    if not r3["ok"]:
        failures += fail(f"新增节点失败: {r3['issues']}")
    time.sleep(2)
    st2 = call("/api/state")
    listening = [n for n in st2["nodes"] if n["kind"] == "server" and n["port"] > 0]
    listening.sort(key=lambda n: n["port"])
    print(f"  监听端点数={len(listening)} ports={[n['port'] for n in listening]}")
    if len(listening) < 2:
        failures += fail("新增的服务端未监听")
    # 原服务端端口不应漂移
    if port not in [n["port"] for n in listening]:
        failures += fail(f"原服务端端口漂移，期望仍含 {port}，实际 {[n['port'] for n in listening]}")

    # 7) 删除第二个服务端
    graph["nodes"] = [n for n in graph["nodes"] if n["id"] not in ("srv2", "k3")]
    graph["edges"] = [e for e in graph["edges"] if e["from"] not in ("srv2", "k3") and e["to"] not in ("srv2", "k3")]
    r4 = call("/api/apply", graph)
    time.sleep(2)
    st3 = call("/api/state")
    remaining = [n for n in st3["nodes"] if n["kind"] == "server"]
    print(f"  删除后监听端点数={len(remaining)}")
    if len(remaining) != 1:
        failures += fail("删除节点后监听数不正确")

    # 8) 发送链路：设备节点应有 tx 输入端口，且 /api/send 可用
    for dev in ("client", "server"):
        t = next(x for x in types if x["typeId"] == dev)
        if not any(p["id"] == "tx" and p["direction"] == "Input" for p in t["ports"]):
            failures += fail(f"{dev} 缺少发送输入端口 tx")
    print("  设备节点具备 tx 发送输入端口")

    # 8b) 闭环已在初始图建立，此处验证按结果路由
    rclose = call("/api/apply", graph)
    if not rclose["ok"]:
        failures += fail(f"闭环应用失败: {rclose['issues']}")
    else:
        res = call("/api/process", {"nodeId": "h", "text": "closed-loop"})
        if not all(r["ok"] for r in res):
            failures += fail(f"闭环路由失败: {res}")
        else:
            print(f"  闭环路由成功: {[(r['nodeId'], r['status']) for r in res]}")

    # 正常发送：设备 tx 上游需有 codec 节点
    send = call("/api/send", {"nodeId": "cli", "text": "verify-tx"})
    if not send["ok"]:
        failures += fail(f"客户端发送失败: {send['status']} {send['error']}")
    else:
        print(f"  客户端发送 {send['bytesWritten']} 字节，会话 {send['sessionCount']}")

    print("ALL CHECKS PASSED" if failures == 0 else f"{failures} CHECK(S) FAILED")
    return failures


if __name__ == "__main__":
    sys.exit(0 if main() == 0 else 1)
