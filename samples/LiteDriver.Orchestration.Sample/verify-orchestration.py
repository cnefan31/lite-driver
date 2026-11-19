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
    for need in ("client", "server", "transform", "handler", "sink"):
        if need not in ids:
            failures += fail(f"缺少节点类型 {need}")
    for gone in ("protocol", "codec"):
        if gone in ids:
            failures += fail(f"协议/编解码器不应再是独立节点: {gone}")

    # 2) 设备节点：tx 发送输入 + out 上报输出；协议与编解码器是自身字段
    for dev in ("client", "server"):
        t = next(x for x in types if x["typeId"] == dev)
        ins = [p["id"] for p in t["ports"] if p["direction"] == "Input"]
        outs = [p["id"] for p in t["ports"] if p["direction"] == "Output"]
        if ins != ["tx"]:
            failures += fail(f"{dev} 输入端口应只有 tx，实际 {ins}")
        if "out" not in outs:
            failures += fail(f"{dev} 缺少 out 输出端口")
        keys = [f["key"] for f in t["fields"]]
        for f in ("kindId", "codecId"):
            if f not in keys:
                failures += fail(f"{dev} 缺少自身字段 {f}")
    print("  设备节点：tx 发送输入 + out 上报输出，kindId/codecId 为自身字段")

    # 3) 设备输出是 IMessage，不能接入只收 IFrame 的端口（现在没有这种端口）
    ok = call("/api/connectable", {
        "fromNodeType": "client", "fromPort": "out",
        "toNodeType": "handler", "toPort": "in"})
    if not ok["ok"]:
        failures += fail("客户端(IMessage)→处理器(IMessage) 应允许")
    print("  客户端→处理器 允许（同为业务消息）")

    # 3) 首次应用：完整链路（客户端→编解码→处理器；服务端 Port=0）
    graph = {
        "revision": 0,
        "nodes": [
            {"id": "srv", "nodeType": "server", "label": "服务端", "x": 60, "y": 340,
             "settings": {"kindId": "length-prefix", "codecId": "text",
                          "host": "127.0.0.1", "port": "0", "maxSessions": "8"}},
            {"id": "cli", "nodeType": "client", "label": "客户端", "x": 60, "y": 90,
             "settings": {"kindId": "length-prefix", "codecId": "text",
                          "endpoints": "127.0.0.1:1"}},
            {"id": "h", "nodeType": "handler", "label": "处理器", "x": 400, "y": 90,
             "settings": {"handlerId": "log", "mode": "log"}},
            {"id": "sk", "nodeType": "sink", "label": "出口", "x": 400, "y": 340,
             "settings": {"sinkId": "queue", "target": "q1"}},
        ],
        "edges": [
            {"id": "e1", "from": "cli", "fromPort": "out", "to": "h", "toPort": "in"},
            {"id": "e2", "from": "srv", "fromPort": "out", "to": "sk", "toPort": "in"},
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
    st = None
    deadline = time.time() + 15
    while time.time() < deadline:
        st = call("/api/state")
        if st["readyClients"] >= 1 and st["inboundSessions"] >= 1:
            break
        time.sleep(0.5)

    print(f"  readyClients={st['readyClients']} inboundSessions={st['inboundSessions']}")
    if st["readyClients"] < 1:
        failures += fail("客户端未就绪")
    if st["inboundSessions"] < 1:
        failures += fail("服务端未收到会话")

    # 6) 运行期新增服务端节点（不重启），必须经编解码器才合法
    graph["nodes"].append({"id": "srv2", "nodeType": "server", "label": "服务端2",
                           "settings": {"kindId": "length-prefix", "codecId": "text",
                                        "host": "127.0.0.1", "port": "0", "maxSessions": "4"}})
    graph["edges"].append({"id": "e5", "from": "srv2", "fromPort": "out", "to": "sk", "toPort": "in"})
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
    graph["nodes"] = [n for n in graph["nodes"] if n["id"] != "srv2"]
    graph["edges"] = [e for e in graph["edges"] if e["from"] != "srv2"]
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

    # 正常发送：节点已配置 codec 且链路就绪，应成功
    send = call("/api/send", {"nodeId": "cli", "text": "verify-tx"})
    if not send["ok"]:
        failures += fail(f"客户端发送失败: {send['status']} {send['error']}")
    else:
        print(f"  客户端发送 {send['bytesWritten']} 字节，会话 {send['sessionCount']}")

    print("ALL CHECKS PASSED" if failures == 0 else f"{failures} CHECK(S) FAILED")
    return failures


if __name__ == "__main__":
    sys.exit(0 if main() == 0 else 1)
