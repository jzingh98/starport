class_name StarportCloudSave
extends Node
## Godot 4 Web wrapper for starport-bridge.v1.js. Methods return a Dictionary with
## {ok, result} or {ok: false, error: {code, message}}. The caller must await them.

class PendingRequest:
	extends RefCounted
	signal done(response: Dictionary)

var _bridge: JavaScriptObject
var _callback: JavaScriptObject
var _pending: Dictionary = {}
var _sequence: int = 0


func _ready() -> void:
	if OS.has_feature("web"):
		_bridge = JavaScriptBridge.get_interface("StarportBridgeV1")
		if _bridge != null:
			# Keep this object alive: Godot callbacks stop working if their reference is freed.
			_callback = JavaScriptBridge.create_callback(_on_response)


func load_save(slot: String) -> Dictionary:
	return await _request("loadSave", {"slot": slot})


func save(slot: String, data: Dictionary, expected_revision: String = "", request_key: String = "") -> Dictionary:
	return await _request("save", {
		"slot": slot,
		"data": data,
		"expectedRevision": expected_revision if expected_revision != "" else null,
		"requestKey": request_key
	})


func list_slots() -> Dictionary:
	return await _request("listSlots", {})


func delete_save(slot: String, expected_revision: String) -> Dictionary:
	return await _request("deleteSave", {"slot": slot, "expectedRevision": expected_revision})


func capabilities() -> Dictionary:
	return await _request("capabilities", {})


func _request(method: String, payload: Dictionary) -> Dictionary:
	if _bridge == null:
		return {"ok": false, "error": {"code": "unavailable", "message": "Starport bridge is only available in a hosted Web game"}}
	_sequence += 1
	var id := "g_%s_%s" % [Time.get_ticks_usec(), _sequence]
	var ticket := PendingRequest.new()
	_pending[id] = ticket
	_bridge.request(id, method, JSON.stringify(payload), _callback)
	return await ticket.done


func _on_response(arguments: Array) -> void:
	if arguments.is_empty():
		return
	var parsed: Variant = JSON.parse_string(str(arguments[0]))
	if not parsed is Dictionary:
		return
	var id: String = str(parsed.get("id", ""))
	if not _pending.has(id):
		return
	var ticket: PendingRequest = _pending[id]
	_pending.erase(id)
	ticket.done.emit(parsed)
