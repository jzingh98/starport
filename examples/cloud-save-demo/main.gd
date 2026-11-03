extends Control
## Freely redistributable counter game for cross-origin save and conflict tests.

var _bridge: StarportCloudSave
var _counter: int = 0
var _revision: String = ""
var _slot: String = "main"
var _autosave_generation: int = 0
var _count_label: Label
var _status_label: Label


func _ready() -> void:
	_bridge = StarportCloudSave.new()
	add_child(_bridge)
	var panel := PanelContainer.new()
	panel.set_anchors_preset(Control.PRESET_FULL_RECT)
	panel.add_theme_constant_override("margin_left", 40)
	panel.add_theme_constant_override("margin_top", 36)
	panel.add_theme_constant_override("margin_right", 40)
	panel.add_theme_constant_override("margin_bottom", 36)
	add_child(panel)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", 14)
	panel.add_child(column)
	var heading := Label.new()
	heading.text = "Starport cloud save demo"
	heading.add_theme_font_size_override("font_size", 30)
	column.add_child(heading)
	var explanation := Label.new()
	explanation.text = "Add stars, save them, reload the page, or open another tab to create a revision conflict."
	column.add_child(explanation)
	_count_label = Label.new()
	_count_label.add_theme_font_size_override("font_size", 62)
	column.add_child(_count_label)
	_add_button(column, "Add a star (autosave)", _increment)
	_add_button(column, "Save now", _save)
	_add_button(column, "Use cloud version", _load)
	_add_button(column, "Keep local as a new slot", _save_as_new_slot)
	_status_label = Label.new()
	_status_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	column.add_child(_status_label)
	_update_count()
	_set_status("Loading cloud save…")
	_load()


func _add_button(parent: VBoxContainer, text: String, action: Callable) -> void:
	var button := Button.new()
	button.text = text
	button.pressed.connect(action)
	parent.add_child(button)


func _increment() -> void:
	_counter += 1
	_update_count()
	_set_status("Local progress changed. Autosaving shortly…")
	_autosave_generation += 1
	var generation := _autosave_generation
	await get_tree().create_timer(0.8).timeout
	if generation == _autosave_generation:
		_save()


func _update_count() -> void:
	_count_label.text = "%s stars" % _counter


func _set_status(message: String) -> void:
	_status_label.text = message


func _load() -> void:
	var response: Dictionary = await _bridge.load_save(_slot)
	if not response.get("ok", false):
		_set_status("Load unavailable: %s. Local progress is untouched." % _error_text(response))
		return
	var result: Variant = response.get("result")
	if result is Dictionary and result.get("data") is Dictionary:
		_counter = int(result["data"].get("counter", 0))
		_revision = str(result.get("revision", ""))
		_update_count()
		_set_status("Loaded slot '%s' at revision %s." % [_slot, _revision])
	else:
		_revision = ""
		_set_status("No cloud save in slot '%s' yet." % _slot)


func _save() -> void:
	var data := {"schemaVersion": 1, "counter": _counter}
	var request_key := "%s-%s" % [Time.get_ticks_usec(), _counter]
	var response: Dictionary = await _bridge.save(_slot, data, _revision, request_key)
	if response.get("ok", false):
		var result: Variant = response.get("result", {})
		if result is Dictionary:
			_revision = str(result.get("revision", _revision))
		_set_status("Saved '%s' to cloud at revision %s." % [_slot, _revision])
	elif _error_code(response) == "conflict":
		_set_status("Save conflict. Choose Use cloud version or Keep local as a new slot. Nothing was overwritten.")
	else:
		_set_status("Save failed: %s. Local progress is untouched." % _error_text(response))


func _save_as_new_slot() -> void:
	_slot = "copy-%s" % int(Time.get_unix_time_from_system())
	_revision = ""
	_save()


func _error_code(response: Dictionary) -> String:
	var error: Variant = response.get("error", {})
	return str(error.get("code", "unknown")) if error is Dictionary else "unknown"


func _error_text(response: Dictionary) -> String:
	var error: Variant = response.get("error", {})
	return str(error.get("message", "Unknown error")) if error is Dictionary else "Unknown error"
