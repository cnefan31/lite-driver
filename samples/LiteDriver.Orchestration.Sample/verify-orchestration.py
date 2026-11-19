#!/usr/bin/env python3
"""编排服务端到端验证：热应用、端口回写、客户端连通、增删节点，全程不重启进程。"""
import json
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:8111"


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

    # 1) 节点类型目录（应含 7 种内置类型）
    types = call("/api/nodetypes")
    ids = [t["typeId"] for t in types]
    print(f"节点类型 {len(types)} 种: {ids}")
    for need in ("protocol", "client", "server", "codec", "transform", "handler", "sink"):
        if need not in ids:
            failures += fail(f"缺少节点类型 {need}")

    # 2) 端口类型约束：客户端不能直连处理器
    bad = call("/api/connectable", {
        "fromNodeType": "client", "fromPort": "in",
        "toNodeType": "handler", "toPort": "in"})
    if bad["ok"]:
        failures += fail("客户端直连处理器未被拒绝")
    else:
        print(f"  客户端→处理器 被拒: {bad['reason']}")

    ok = call("/api/connectable", {
        "fromNodeType": "client", "fromPort": "in",
        "toNodeType": "codec", "toPort": "in"})
    if not ok["ok"]:
        failures += fail("客户端→编解码器 被误拒")
    else:
        print("  客户端→编解码器 允许")

    # 3) 首次应用：完整链路（客户端→编解码→处理器；服务端 Port=0）
    graph = {
        "revision": 0,
        "nodes": [
            {"id": "p", "nodeType": "protocol", "label": "协议", "x": 40, "y": 210,
             "settings": {"kindId": "length-prefix"}},
            {"id": "srv", "nodeType": "server", "label": "服务端", "x": 270, "y": 340,
             "settings": {"host": "127.0.0.1", "port": "0", "maxSessions": "8"}},
            {"id": "cli", "nodeType": "client", "label": "客户端", "x": 270, "y": 90,
             "settings": {"endpoints": "127.0.0.1:1"}},
            {"id": "k", "nodeType": "codec", "label": "编解码器", "x": 500, "y": 90,
             "settings": {"codecId": "text"}},
            {"id": "h", "nodeType": "handler", "label": "处理器", "x": 730, "y": 90,
             "settings": {"handlerId": "log", "mode": "log"}},
        ],
        "edges": [
            {"id": "e1", "from": "p", "fromPort": "out", "to": "srv", "toPort": "codec"},
            {"id": "e2", "from": "p", "fromPort": "out", "to": "cli", "toPort": "codec"},
            {"id": "e3", "from": "cli", "fromPort": "in", "to": "k", "toPort": "in"},
            {"id": "e4", "from": "k", "fromPort": "out", "to": "h", "toPort": "in"},
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

    # 4) 校验：客户端直连处理器必须被拒，且定位到节点
    bad = json.loads(json.dumps(graph))
    bad["edges"] = [e for e in bad["edges"] if e["id"] != "e3"]
    bad["edges"].append({"id": "eBad", "from": "cli", "fromPort": "in", "to": "h", "toPort": "in"})
    v = call("/api/validate", bad)
    if v["ok"]:
        failures += fail("孤立服务端节点未被拒绝")
    else:
        assert any(i.get("code") == "edge_type_mismatch" for i in v["issues"]), "未报类型不匹配"
        print(f"  校验拒绝且定位到节点: {v['issues'][0]['code']}")

    # 5) 热更新客户端指向真实端口 -> 应连通
    graph["nodes"][1]["settings"]["port"] = str(port)
    graph["nodes"][2]["settings"]["endpoints"] = f"127.0.0.1:{port}"
    r2 = call("/api/apply", graph)
    if not r2["ok"]:
        failures += fail(f"热更新失败: {r2['issues']}")
    time.sleep(4)
    st = call("/api/state")
    print(f"  readyClients={st['readyClients']} inboundSessions={st['inboundSessions']}")
    if st["readyClients"] < 1:
        failures += fail("客户端未就绪")
    if st["inboundSessions"] < 1:
        failures += fail("服务端未收到会话")

    # 6) 运行期新增服务端节点（不重启），必须经编解码器才合法
    graph["nodes"].append({"id": "srv2", "nodeType": "server", "label": "服务端2",
                           "settings": {"host": "127.0.0.1", "port": "0", "maxSessions": "4"}})
    graph["edges"].append({"id": "e5", "from": "p", "fromPort": "out", "to": "srv2", "toPort": "codec"})
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
    graph["edges"] = [e for e in graph["edges"] if e["to"] != "srv2"]
    r4 = call("/api/apply", graph)
    time.sleep(2)
    st3 = call("/api/state")
    remaining = [n for n in st3["nodes"] if n["kind"] == "server"]
    print(f"  删除后监听端点数={len(remaining)}")
    if len(remaining) != 1:
        failures += fail("删除节点后监听数不正确")

    print("ALL CHECKS PASSED" if failures == 0 else f"{failures} CHECK(S) FAILED")
    return failures


if __name__ == "__main__":
    sys.exit(0 if main() == 0 else 1)
