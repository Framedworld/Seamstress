import QtQuick
import QtQuick.Controls
import QtQuick.Dialogs
import QtQuick.Layouts
import QtCore

ApplicationWindow {
    id: window
    visible: true
    width: App.startupWidth > 0 ? App.startupWidth : 1500
    height: App.startupHeight > 0 ? App.startupHeight : 900
    minimumWidth: 900
    minimumHeight: 560
    title: App.windowTitle

    // The colour scheme shared by every control, from the theme chosen in Preferences ▸ Themes.
    palette {
        window: App.themeWindow; windowText: App.themeWindowText
        base: App.themeBase; alternateBase: App.themeAlternateBase; text: App.themeText
        button: App.themeButton; buttonText: App.themeButtonText
        highlight: App.themeHighlight; highlightedText: App.themeHighlightedText
        mid: App.themeMid; dark: App.themeDark; light: App.themeLight; midlight: App.themeMidlight; shadow: App.themeShadow
        placeholderText: App.themePlaceholderText; toolTipBase: App.themeToolTipBase; toolTipText: App.themeToolTipText
        brightText: App.themeBrightText; link: App.themeLink
    }

    // Chinese and Japanese draw some of the same characters (and punctuation) differently, so use the first regional
    // CJK font that is installed; with none of them, or in other languages, the usual font and its fallbacks apply.
    readonly property var cjkFonts: ({
        zh: ["Noto Sans CJK SC", "Source Han Sans SC", "Microsoft YaHei UI", "PingFang SC"],
        ja: ["Noto Sans CJK JP", "Source Han Sans JP", "Yu Gothic UI", "Meiryo UI", "Hiragino Sans"],
    })
    readonly property string cjkFont: {
        const installed = Qt.fontFamilies()
        return (cjkFonts[App.settingLanguage] || []).find(f => installed.indexOf(f) >= 0) || ""
    }
    font.family: cjkFont !== "" ? cjkFont : Qt.application.font.family

    property bool viewportConfirmed: false
    property var afterDiscard: null      // what to do once unsaved changes were dealt with
    property bool closing: false
    property string pendingExport: ""
    // Where Help sends readers for the manual; the guide lives in the repository's docs folder.
    readonly property string userGuideUrl: "https://github.com/MangoDragonHub/Seamstress/blob/main/docs/USER_GUIDE.md"
    // A text box has the keyboard: Enter and Escape belong to it rather than to the tool shortcuts.
    readonly property bool typing: activeFocusItem !== null && activeFocusItem.cursorPosition !== undefined
    // The toolbar icons are monochrome and take their colour from the palette.
    readonly property string toolIcons: "qrc:/assemblies/Seamstress/icons/tools/"
    // Interface texts in the language chosen in Preferences; reading the revision redoes the binding when it changes.
    function tr(text) { App.languageRevision; return App.tr(text) }

    onFrameSwapped: {
        if (App.startupAutomated)
            App.frameDrawn()
        if (!viewportConfirmed) {
            viewportConfirmed = true
            App.viewportReady()
        }
    }

    onClosing: (close) => {
        if (App.dirty && !closing) {
            close.accepted = false
            guard(() => { closing = true; window.close() })
        } else {
            App.shutdown()
        }
    }

    // Runs an action immediately, or after asking what to do with unsaved changes.
    function guard(action) {
        if (!App.dirty) {
            action()
            return
        }
        afterDiscard = action
        unsavedDialog.open()
    }

    function save(then) {
        if (App.hasFile) {
            if (App.save() && then) then()
        } else {
            saveDialog.then = then || null
            saveDialog.currentFile = saveDialog.currentFolder + "/" + App.suggestedFileName(".garment.json")
            saveDialog.open()
        }
    }

    function startExport(format) {
        pendingExport = format
        exportOptions.open()
    }

    function startSvgExport() {
        svgDialog.currentFile = svgDialog.currentFolder + "/" + App.suggestedFileName(".svg")
        svgDialog.open()
    }

    // Files dropped on the window: a project is opened (after asking about unsaved changes), a body becomes the
    // avatar, a picture becomes a reference image behind the pattern. Returns how many files were used.
    function openDropped(urls) {
        let used = 0, project = ""
        for (const dropped of urls) {
            const url = dropped.toString()
            const name = url.toLowerCase()
            if (name.endsWith(".garment.json") || name.endsWith(".json")) {
                if (project === "") project = url
            } else if (App.isAvatarFile(url)) {
                App.setAvatar("CustomObj", url, 1)
                used++
            } else if (/\.(png|jpe?g|gif|webp|bmp)$/.test(name)) {
                patternView.addReferenceImage(url)
                used++
            }
        }
        if (project !== "") {
            window.guard(() => App.open(project))
            used++
        }
        return used
    }

    // Applies the newest simulation frame about 30 times a second; cheap when nothing is running.
    Timer { interval: 33; running: true; repeat: true; onTriggered: App.tick() }

    // Used by automated runs: capture the window, then quit. Every way this can go wrong ends the run with a reason
    // on standard error (App.automationFailed); a run that never gets here is ended by the watchdog in Program.cs.
    Timer {
        interval: App.startupSmoke ? 6000 : 5000
        running: App.startupScreenshot !== ""
        onTriggered: {
            App.automationStep("the capture timer fired")
            if (App.startupSmoke && !(smokeLoader.item && smokeLoader.item.finished)) {
                App.automationFailed("the smoke test did not run to its end")
                return
            }
            if (App.startupSmoke && smokeLoader.item.failures > 0) {
                App.automationFailed("the smoke test failed: " + smokeLoader.item.failures + " check(s)")
                return
            }
            // "--view prefs|export|makehuman|garment" captures that dialog or the 3D garment viewport directly.
            const view = App.startupView
            // "--view picture" (with wide3d, a camera...) saves the 3D view the way "Save 3D View as Image" does: at
            // twice its size, without the toolbar, the floor grid or the background, for comparing with artwork.
            if (view.indexOf("picture") >= 0) {
                const asked = viewport.saveImage(App.startupScreenshot, 2, true, function(saved) {
                    if (!saved) {
                        App.automationFailed("could not write the picture " + App.startupScreenshot)
                        return
                    }
                    App.automationDone()
                    window.closing = true
                    Qt.quit()
                })
                if (asked)
                    App.automationStep("the picture was requested and waits for the next frame")
                else
                    App.automationFailed("the picture could not be started")
                return
            }
            const dialog = view.indexOf("prefs") >= 0 ? preferences : view.indexOf("export") >= 0 ? exportOptions
                         : view.indexOf("makehuman") >= 0 ? makeHumanDialog
                         : view.indexOf("bodyshape") >= 0 ? bodyShape
                         : view.indexOf("saveimage") >= 0 ? imageOptions
                         : view.indexOf("recovery") >= 0 ? recoveryDialog : null
            const target = dialog ? dialog.contentItem.parent
                         : view.indexOf("contextmenu") >= 0 ? contextMenu.contentItem.parent
                         : view.indexOf("garment") >= 0 ? viewport
                         : view.indexOf("pattern") >= 0 ? patternView
                         : view.indexOf("properties") >= 0 ? propertiesPanel : rootLayout
            if (!target || target.width <= 0 || target.height <= 0) {
                App.automationFailed("there is nothing to capture for --view \"" + view + "\" (it is not open or has no size)")
                return
            }
            const requested = target.grabToImage(function(result) {
                if (!result.saveToFile(App.startupScreenshot)) {
                    App.automationFailed("could not write the screenshot " + App.startupScreenshot)
                    return
                }
                App.automationDone()
                window.closing = true
                Qt.quit()
            })
            if (requested)
                App.automationStep("the capture was requested and waits for the next frame")
            else
                App.automationFailed("the capture could not be started for --view \"" + view + "\"")
        }
    }

    Timer {
        interval: 1500
        running: App.startupScreenshot !== ""
        onTriggered: {
            const view = App.startupView
            // "--view prefskeyboard" (or prefsviewport, …) opens Preferences on that page.
            if (view.indexOf("prefs") >= 0) preferences.openPage(preferences.pages.find(p => view.indexOf("prefs" + p.toLowerCase()) >= 0) || "General")
            if (view.indexOf("contextmenu") >= 0) contextMenu.popup(patternView, patternView.width * 0.45, patternView.height * 0.35)
            if (view.indexOf("export") >= 0) { window.pendingExport = "fbx"; exportOptions.open() }
            if (view.indexOf("makehuman") >= 0) makeHumanDialog.open()
            if (view.indexOf("bodyshape") >= 0) bodyShape.open()
            if (view.indexOf("saveimage") >= 0) imageOptions.open()
            // "--drop <file>" (once per file) does what dropping those files on the window does.
            if (App.startupDrops !== "") window.openDropped(App.startupDrops.split("|"))
        }
    }

    // Automated interface check ("--smoke <folder>"), loaded only when asked for.
    Loader {
        id: smokeLoader
        active: App.startupSmoke
        source: "SmokeTest.qml"
        onLoaded: {
            item.folder = App.startupFolderUrl
            item.run()
            smokePicture.start()
            smokeFinish.start()
        }
    }
    Timer { id: smokePicture; interval: 1500; onTriggered: smokeLoader.item.picture() }
    Timer { id: smokeFinish; interval: 4000; onTriggered: smokeLoader.item.finish() }

    Connections {
        target: App
        function onErrorRevisionChanged() {
            if (App.startupSmoke) {
                App.log("  ERROR " + App.lastError)
                return
            }
            errorDialog.text = App.lastError
            errorDialog.open()
        }
    }

    // ------------------------------------------------------------------ shortcuts
    // The keys come from App.keyBindingsJson() and can be changed in Preferences ▸ Keyboard; this table says what
    // each command does.
    readonly property var commands: ({
        "file.new": () => newAction.trigger(),
        "file.open": () => openAction.trigger(),
        "file.save": () => saveAction.trigger(),
        "file.saveAs": () => saveAsAction.trigger(),
        "file.preferences": () => preferences.open(),
        "file.quit": () => window.close(),
        "edit.undo": () => App.undo(),
        "edit.redo": () => App.redo(),
        "edit.selectAll": () => App.selectAll(),
        "edit.duplicate": () => App.duplicate(),
        "edit.delete": () => App.deleteSelection(),
        "edit.mirror": () => App.mirror(),
        "edit.rotateLeft": () => App.rotate(90),
        "edit.rotateRight": () => App.rotate(-90),
        "edit.straighten": () => App.straightenEdges(),
        "edit.group": () => App.groupSelection(),
        "edit.reverseSeam": () => App.reverseSeam(),
        "tool.select": () => App.tool = "Select",
        "tool.editPoints": () => App.tool = "EditPoints",
        "tool.transformPoints": () => App.tool = "TransformPoints",
        "tool.sew": () => App.tool = "Sew",
        "tool.insetBand": () => App.tool = "InsetBand",
        "tool.cut": () => App.tool = "Cut",
        "tool.polygon": () => App.tool = "Polygon",
        "tool.rectangle": () => App.tool = "Rectangle",
        "tool.ellipse": () => App.tool = "Ellipse",
        "tool.ruler": () => App.tool = "Ruler",
        "tool.confirm": () => App.confirmTool(),
        "tool.cancel": () => { viewport.clearRuler(); if (App.pinBrush) App.pinBrush = false; else App.cancelTool() },
        "handles.full": () => App.gizmoMode = "Full",
        "handles.move": () => App.gizmoMode = "Move",
        "handles.rotate": () => App.gizmoMode = "Rotate",
        "handles.scale": () => App.gizmoMode = "Scale",
        "view.uv": () => App.uvMode = !App.uvMode,
        "view.frame": () => patternView.frameAll(),
        "view.avatar2d": () => App.showBodyOutline = !App.showBodyOutline,
        // The orbit is a mouse gesture read by the 3D view's mouse area; a key given to it here does nothing.
        "view.orbit": () => {},
        "view.ortho": () => viewport.orthographic = !viewport.orthographic,
        "view.front": () => viewport.setView(0, -5),
        "view.side": () => viewport.setView(90, -5),
        "view.back": () => viewport.setView(180, -5),
        "view.checker": () => viewport.displayMode = viewport.displayMode === "checker" ? "shaded" : "checker",
        "view.wireframe": () => viewport.wireframe = !viewport.wireframe,
        "view.normals": () => viewport.showNormals = !viewport.showNormals,
        "edit.invertNormals": () => App.invertNormals(),
        "edit.cutHole": () => App.cutHole(),
        "view.pinch": () => viewport.pinchMode = !viewport.pinchMode,
        "view.rip": () => viewport.ripMode = !viewport.ripMode,
        "view.pinBrush": () => App.pinBrush = !App.pinBrush,
        "view.handles": () => viewport.showGizmo = !viewport.showGizmo,
        "view.avatar": () => viewport.showAvatar = !viewport.showAvatar,
        "sim.toggle": () => App.toggleSimulation(),
        "sim.reset": () => App.resetArrangement(),
        "sim.setCreases": () => App.setCreases(),
        "sim.clearCreases": () => App.clearCreases(),
    })
    function run(id) { if (commands[id]) commands[id]() }
    readonly property var bindings: { App.keysRevision; return JSON.parse(App.keyBindingsJson()) }
    // Mouse-button gestures (the 3D orbit) never come from the keyboard; the 3D view's mouse area reads them.
    function keyboardKeys(keys) { return keys.filter(k => /Mouse(Left|Middle|Right)/.test(k) === false) }
    // The first key of a command, for tooltips: " (V)", or nothing when it has no key.
    function keyHint(id) { App.keysRevision; const k = App.keyText(id); return k === "" ? "" : " (" + k + ")" }
    // Set while Preferences listens for a new key, so pressing it does not also run its command.
    readonly property bool recordingKey: preferences.recording

    Item {
        Repeater {
            model: window.bindings
            delegate: Item {
                id: holder
                required property var modelData
                Shortcut {
                    // While typing, keys without Ctrl, Alt or Meta belong to the text box.
                    sequences: window.typing ? window.keyboardKeys(holder.modelData.keys).filter(k => /(^|\+)(Ctrl|Alt|Meta)\+/.test(k))
                                             : window.keyboardKeys(holder.modelData.keys)
                    enabled: !window.recordingKey && sequences.length > 0
                    onActivated: window.run(holder.modelData.id)
                }
            }
        }
    }

    Action { id: newAction; text: tr("&New"); onTriggered: window.guard(() => App.newDocument()) }
    Action { id: openAction; text: tr("&Open…"); onTriggered: window.guard(() => openDialog.open()) }
    Action { id: saveAction; text: tr("&Save"); onTriggered: window.save(null) }
    Action {
        id: saveAsAction
        text: tr("Save &As…")
        onTriggered: {
            saveDialog.then = null
            saveDialog.currentFile = saveDialog.currentFolder + "/" + App.suggestedFileName(".garment.json")
            saveDialog.open()
        }
    }

    ColumnLayout {
        id: rootLayout
        anchors.fill: parent
        spacing: 0

        // ------------------------------------------------------------------ menus
        MenuBar {
            Layout.fillWidth: true
            Menu {
                title: tr("&File")
                KeyMenuItem { action: newAction; command: "file.new" }
                KeyMenuItem { action: openAction; command: "file.open" }
                Menu {
                    id: recentMenu
                    title: tr("Open &Recent")
                    property var files: []
                    enabled: files.length > 0
                    onAboutToShow: files = App.recentFiles().split("|").filter(f => f !== "")
                    Component.onCompleted: files = App.recentFiles().split("|").filter(f => f !== "")
                    Instantiator {
                        model: recentMenu.files
                        delegate: MenuItem {
                            required property string modelData
                            text: App.fileName(modelData)
                            onTriggered: window.guard(() => App.open(modelData))
                        }
                        onObjectAdded: (index, object) => recentMenu.insertItem(index, object)
                        onObjectRemoved: (index, object) => recentMenu.removeItem(object)
                    }
                }
                MenuSeparator {}
                KeyMenuItem { action: saveAction; command: "file.save" }
                KeyMenuItem { action: saveAsAction; command: "file.saveAs" }
                MenuSeparator {}
                Menu {
                    title: tr("&Export")
                    MenuItem { text: tr("Wavefront &OBJ…"); onTriggered: window.startExport("obj") }
                    MenuItem { text: tr("&glTF 2.0 (.glb / .gltf)…"); onTriggered: window.startExport("gltf") }
                    MenuItem { text: tr("&FBX…"); onTriggered: window.startExport("fbx") }
                    MenuSeparator {}
                    MenuItem { text: tr("Export Pattern as &SVG…"); onTriggered: window.startSvgExport() }
                    MenuSeparator {}
                    MenuItem { text: tr("Send to &MakeHuman"); onTriggered: makeHumanDialog.open() }
                }
                MenuItem { text: tr("Save 3D View as &Image…"); onTriggered: imageOptions.open() }
                MenuSeparator {}
                KeyMenuItem { text: tr("&Preferences…"); command: "file.preferences"; onTriggered: preferences.open() }
                MenuSeparator {}
                KeyMenuItem { text: tr("&Quit"); command: "file.quit"; onTriggered: window.close() }
            }
            EditMenu {}
            Menu {
                id: garmentMenu
                title: tr("&Garment")
                MenuItem { text: tr("New Empty Garment"); onTriggered: App.addGarment() }
                MenuItem { text: tr("New Group"); onTriggered: App.addGroup() }
                MenuSeparator {}
                Instantiator {
                    model: App.templateNames().split("|")
                    delegate: MenuItem {
                        required property string modelData
                        text: tr("Add %1").arg(tr(modelData))
                        onTriggered: App.addTemplate(modelData)
                    }
                    onObjectAdded: (index, object) => garmentMenu.insertItem(index + 3, object)
                    onObjectRemoved: (index, object) => garmentMenu.removeItem(object)
                }
                MenuSeparator {}
                MenuItem { text: tr("Import Mesh as Pattern (OBJ)..."); onTriggered: importMeshDialog.open() }
            }
            Menu {
                title: tr("&Simulation")
                KeyMenuItem { text: App.simulating ? tr("Stop Simulation") : tr("Simulate"); command: "sim.toggle"; onTriggered: App.toggleSimulation() }
                MenuItem {
                    text: tr("Simulate All Garments")
                    checkable: true
                    checked: App.simulateAll
                    onTriggered: App.simulateAll = checked
                }
                MenuSeparator {}
                KeyMenuItem { text: tr("Reset Arrangement"); command: "sim.reset"; onTriggered: App.resetArrangement() }
                KeyMenuItem { text: tr("Set Creases"); command: "sim.setCreases"; onTriggered: App.setCreases() }
                KeyMenuItem { text: tr("Clear Creases"); command: "sim.clearCreases"; onTriggered: App.clearCreases() }
                MenuSeparator {}
                // The pin actions of the 3D pin brush's right-click menu (PinMenu.qml), for the vertices chosen with it.
                MenuItem { text: tr("Pin Selected Vertices"); enabled: App.pinSelectionCount > 0; onTriggered: App.pinSelectedVertices() }
                MenuItem { text: tr("Unpin Selected Vertices"); enabled: App.pinSelectionCount > 0; onTriggered: App.unpinSelectedVertices() }
                MenuItem { text: tr("Clear Vertex Selection"); enabled: App.pinSelectionCount > 0; onTriggered: App.clearPinSelection() }
                MenuItem { text: tr("Unpin All on Selected Garments"); onTriggered: App.unpinAll() }
            }
            Menu {
                title: tr("&Avatar")
                MenuItem { text: tr("Mannequin"); checkable: true; checked: App.avatarType === "Mannequin"; onTriggered: App.setAvatar("Mannequin", "", 1) }
                MenuItem { text: tr("MakeHuman Base Mesh"); checkable: true; checked: App.avatarType === "MakeHuman"; onTriggered: App.setAvatar("MakeHuman", "", 1) }
                MenuItem { text: tr("MakeHuman Body Shape…"); enabled: App.avatarType === "MakeHuman"; onTriggered: bodyShape.open() }
                MenuItem { text: tr("Load Body (OBJ, FBX, glTF)…"); checkable: true; checked: App.avatarType === "CustomObj"; onTriggered: avatarDialog.open() }
                MenuItem { text: tr("None"); checkable: true; checked: App.avatarType === "None"; onTriggered: App.setAvatar("None", "", 1) }
            }
            Menu {
                title: tr("&View")
                MenuItem { text: tr("2D: Pattern"); checkable: true; checked: !App.uvMode; onTriggered: App.uvMode = false }
                KeyMenuItem { text: tr("2D: UV Layout"); command: "view.uv"; checkable: true; checked: App.uvMode; onTriggered: App.uvMode = true }
                KeyMenuItem { text: tr("2D: Show Avatar"); command: "view.avatar2d"; checkable: true; checked: App.showBodyOutline; onTriggered: App.showBodyOutline = checked }
                Menu {
                    id: referencesMenu
                    title: tr("References")
                    property var refs: []
                    onAboutToShow: refs = JSON.parse(App.references())
                    Component.onCompleted: refs = JSON.parse(App.references())
                    MenuItem { text: tr("Add Reference Image…"); enabled: !App.uvMode; onTriggered: referenceDialog.open() }
                    MenuItem { text: tr("Show References"); checkable: true; checked: App.showReferences; onTriggered: App.showReferences = checked }
                    MenuSeparator {}
                    Instantiator {
                        model: referencesMenu.refs
                        delegate: MenuItem {
                            required property var modelData
                            text: (modelData.selected ? "•  " : "   ")
                                  + (modelData.missing ? "⚠  " : "")
                                  + modelData.name
                                  + (modelData.visible ? "" : "  (" + tr("(hidden)") + ")")
                                  + (modelData.locked ? "  (" + tr("(locked)") + ")" : "")
                            onTriggered: App.selectReference(modelData.id)
                        }
                        onObjectAdded: (index, object) => referencesMenu.insertItem(index + 3, object)
                        onObjectRemoved: (index, object) => referencesMenu.removeItem(object)
                    }
                    MenuSeparator {}
                    MenuItem { text: tr("Remove All References"); enabled: referencesMenu.refs.some(r => !r.locked); onTriggered: App.removeAllReferences() }
                }
                KeyMenuItem { text: tr("Frame All in 2D"); command: "view.frame"; onTriggered: patternView.frameAll() }
                MenuSeparator {}
                KeyMenuItem { text: tr("3D: Orthographic"); command: "view.ortho"; checkable: true; checked: viewport.orthographic; onTriggered: viewport.orthographic = checked }
                KeyMenuItem { text: tr("3D: Front"); command: "view.front"; onTriggered: viewport.setView(0, -5) }
                KeyMenuItem { text: tr("3D: Side"); command: "view.side"; onTriggered: viewport.setView(90, -5) }
                KeyMenuItem { text: tr("3D: Back"); command: "view.back"; onTriggered: viewport.setView(180, -5) }
                MenuSeparator {}
                KeyMenuItem { text: tr("3D: Checker (Texel Density)"); command: "view.checker"; checkable: true; checked: viewport.displayMode === "checker"; onTriggered: viewport.displayMode = checked ? "checker" : "shaded" }
                KeyMenuItem { text: tr("3D: Wireframe (Topology)"); command: "view.wireframe"; checkable: true; checked: viewport.wireframe; onTriggered: viewport.wireframe = checked }
                KeyMenuItem { text: tr("3D: Normal Direction"); command: "view.normals"; checkable: true; checked: viewport.showNormals; onTriggered: viewport.showNormals = checked }
                KeyMenuItem { text: tr("3D: Pinch Cloth"); command: "view.pinch"; checkable: true; checked: viewport.pinchMode; onTriggered: viewport.pinchMode = checked }
                KeyMenuItem { text: tr("3D: Rip Cloth"); command: "view.rip"; checkable: true; checked: viewport.ripMode; onTriggered: viewport.ripMode = checked }
                KeyMenuItem { text: tr("3D: Pin Vertices"); command: "view.pinBrush"; checkable: true; checked: App.pinBrush; onTriggered: App.pinBrush = checked }
                KeyMenuItem { text: tr("3D: Show Handles"); command: "view.handles"; checkable: true; checked: viewport.showGizmo; onTriggered: viewport.showGizmo = checked }
                Menu {
                    title: tr("Handles")
                    KeyMenuItem { text: tr("Move, Rotate and Scale"); command: "handles.full"; checkable: true; checked: App.gizmoMode === "Full"; onTriggered: App.gizmoMode = "Full" }
                    KeyMenuItem { text: tr("Move"); command: "handles.move"; checkable: true; checked: App.gizmoMode === "Move"; onTriggered: App.gizmoMode = "Move" }
                    KeyMenuItem { text: tr("Rotate"); command: "handles.rotate"; checkable: true; checked: App.gizmoMode === "Rotate"; onTriggered: App.gizmoMode = "Rotate" }
                    KeyMenuItem { text: tr("Scale"); command: "handles.scale"; checkable: true; checked: App.gizmoMode === "Scale"; onTriggered: App.gizmoMode = "Scale" }
                }
                KeyMenuItem { text: tr("3D: Show Avatar"); command: "view.avatar"; checkable: true; checked: viewport.showAvatar; onTriggered: viewport.showAvatar = checked }
                MenuItem { text: tr("3D: Show Sewing Threads"); checkable: true; checked: viewport.showThreads; onTriggered: viewport.showThreads = checked }
            }
            Menu {
                title: tr("&Help")
                MenuItem { text: tr("Documentation"); onTriggered: Qt.openUrlExternally(window.userGuideUrl) }
                MenuSeparator {}
                MenuItem { text: tr("About Seamstress"); onTriggered: aboutDialog.open() }
            }
        }

        // ------------------------------------------------------------------ toolbar
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: 1
            color: palette.mid
            opacity: 0.75
        }
        ToolBar {
            Layout.fillWidth: true
            implicitHeight: 44
            background: Rectangle {
                color: palette.window
                Rectangle {
                    anchors { left: parent.left; right: parent.right; bottom: parent.bottom }
                    height: 1
                    color: palette.mid
                }
            }
            RowLayout {
                anchors.fill: parent
                anchors.leftMargin: 10
                anchors.rightMargin: 10
                spacing: 8

                component ToolGroup: Rectangle {
                    default property alias items: groupRow.data
                    implicitWidth: groupRow.implicitWidth + 6
                    implicitHeight: 32
                    radius: 6
                    color: Qt.alpha(palette.dark, 0.6)
                    border.color: Qt.alpha(palette.mid, 0.9)
                    border.width: 1
                    RowLayout {
                        id: groupRow
                        anchors.centerIn: parent
                        spacing: 2
                    }
                }
                component StudioIconButton: ToolButton {
                    id: studioBtn
                    display: AbstractButton.IconOnly
                    implicitWidth: 28
                    implicitHeight: 26
                    padding: 3
                    icon.width: 18
                    icon.height: 18
                    icon.color: !enabled ? palette.placeholderText
                        : (checked ? palette.highlightedText : palette.buttonText)
                    background: Rectangle {
                        radius: 5
                        color: !studioBtn.enabled ? "transparent"
                            : studioBtn.checked ? (studioBtn.hovered ? Qt.lighter(palette.highlight, 1.08) : palette.highlight)
                            : studioBtn.down ? Qt.alpha(palette.windowText, 0.16)
                            : studioBtn.hovered ? Qt.alpha(palette.windowText, 0.10)
                            : "transparent"
                        Behavior on color { ColorAnimation { duration: 90 } }
                    }
                }
                component ToolChoice: StudioIconButton {
                    property string tool: ""
                    property string label: ""
                    property string tip: ""
                    property string command: ""
                    property string iconFile: ""
                    checkable: true
                    checked: App.tool === tool && !App.uvMode
                    enabled: !App.uvMode || tool === "Select"
                    icon.source: window.toolIcons + iconFile
                    Accessible.name: label
                    Accessible.description: tip
                    onClicked: App.tool = tool
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tip + window.keyHint(command)
                }

                ToolGroup {
                    ToolChoice { label: tr("Select"); tool: "Select"; command: "tool.select"; iconFile: "select.svg"; tip: tr("Select and move pieces") }
                    ToolChoice { label: tr("Edit Points"); tool: "EditPoints"; command: "tool.editPoints"; iconFile: "edit-points.svg"; tip: tr("Move points, curve edges, add points") }
                    ToolChoice { label: tr("Transform Points"); tool: "TransformPoints"; command: "tool.transformPoints"; iconFile: "transform-points.svg"; tip: tr("Select points and move, rotate or scale them with handles") }
                    ToolChoice { label: tr("Sew"); tool: "Sew"; command: "tool.sew"; iconFile: "sew.svg"; tip: tr("Click two edges to sew them together") }
                    ToolChoice { label: tr("Inset Band"); tool: "InsetBand"; command: "tool.insetBand"; iconFile: "inset-band.svg"; tip: tr("Cut a band (a waistband, collar band or facing) off pieces along the edges you click, already sewn on") }
                    ToolChoice { label: tr("Cut"); tool: "Cut"; command: "tool.cut"; iconFile: "cut.svg"; tip: tr("Draw a line across pieces to cut them into separate pieces") }
                }

                ToolGroup {
                    ToolChoice { label: tr("Polygon"); tool: "Polygon"; command: "tool.polygon"; iconFile: "polygon.svg"; tip: tr("Draw a piece corner by corner") }
                    ToolChoice { label: tr("Rectangle"); tool: "Rectangle"; command: "tool.rectangle"; iconFile: "rectangle.svg"; tip: tr("Drag out a rectangular piece") }
                    ToolChoice { label: tr("Ellipse"); tool: "Ellipse"; command: "tool.ellipse"; iconFile: "ellipse.svg"; tip: tr("Drag out an elliptical piece") }
                }

                ToolGroup {
                    ToolChoice { label: tr("Ruler"); tool: "Ruler"; command: "tool.ruler"; iconFile: "ruler.svg"; tip: tr("Measure distances in the 2D view, or between two points on the cloth or avatar in the 3D view") }
                }

                ToolGroup {
                    StudioIconButton {
                        icon.source: window.toolIcons + "undo.svg"
                        enabled: App.canUndo
                        Accessible.name: tr("Undo")
                        Accessible.description: App.undoText
                        onClicked: App.undo()
                        ToolTip.visible: hovered
                        ToolTip.text: App.undoText
                    }
                    StudioIconButton {
                        icon.source: window.toolIcons + "redo.svg"
                        enabled: App.canRedo
                        Accessible.name: tr("Redo")
                        Accessible.description: App.redoText
                        onClicked: App.redo()
                        ToolTip.visible: hovered
                        ToolTip.text: App.redoText
                    }
                }

                ToolGroup {
                    StudioIconButton {
                        icon.source: window.toolIcons + (App.uvMode ? "uv-layout.svg" : "pattern.svg")
                        checkable: true
                        checked: App.uvMode
                        Accessible.name: App.uvMode ? tr("UV Layout") : tr("Pattern")
                        Accessible.description: tr("Switch the 2D view between the pattern and its UV layout")
                        onClicked: App.uvMode = checked
                        ToolTip.visible: hovered
                        ToolTip.text: tr("Switch the 2D view between the pattern and its UV layout") + window.keyHint("view.uv")
                    }
                }

                Item { Layout.fillWidth: true }

                CheckBox {
                    text: tr("All garments")
                    checked: App.simulateAll
                    font.pixelSize: 12
                    onToggled: App.simulateAll = checked
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Off: only the active garment (and garments linked with ⇄) is simulated.\nOn: every visible garment is simulated.")
                }
                Button {
                    id: simulateButton
                    text: App.simulating ? "■  " + tr("Stop") : "▶  " + tr("Simulate")
                    implicitWidth: 118
                    implicitHeight: 30
                    highlighted: true
                    hoverEnabled: true
                    onClicked: App.toggleSimulation()
                    contentItem: Label {
                        text: simulateButton.text
                        font.pixelSize: 12
                        font.weight: Font.DemiBold
                        color: "#ffffff"
                        horizontalAlignment: Text.AlignHCenter
                        verticalAlignment: Text.AlignVCenter
                    }
                    background: Rectangle {
                        radius: 6
                        color: App.simulating
                            ? (simulateButton.down ? "#b91c1c" : simulateButton.hovered ? "#dc2626" : "#ef4444")
                            : (simulateButton.down ? "#047857" : simulateButton.hovered ? "#059669" : "#10b981")
                        border.width: 1
                        border.color: App.simulating ? Qt.alpha("#fca5a5", 0.35) : Qt.alpha("#6ee7b7", 0.35)
                        Behavior on color { ColorAnimation { duration: 100 } }
                    }
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Sew and drape the active garment") + window.keyHint("sim.toggle")
                }
                Button {
                    id: resetButton
                    text: tr("Reset")
                    implicitHeight: 30
                    leftPadding: 14
                    rightPadding: 14
                    hoverEnabled: true
                    onClicked: App.resetArrangement()
                    contentItem: Label {
                        text: resetButton.text
                        font.pixelSize: 12
                        font.weight: Font.Medium
                        color: palette.buttonText
                        horizontalAlignment: Text.AlignHCenter
                        verticalAlignment: Text.AlignVCenter
                    }
                    background: Rectangle {
                        radius: 6
                        color: resetButton.down ? palette.dark
                            : resetButton.hovered ? palette.midlight
                            : palette.button
                        border.width: 1
                        border.color: resetButton.hovered ? palette.light : palette.mid
                        Behavior on color { ColorAnimation { duration: 90 } }
                    }
                    ToolTip.visible: hovered
                    ToolTip.delay: 500
                    ToolTip.text: tr("Return the pieces to their flat arrangement")
                }
            }
        }

        // ------------------------------------------------------------------ workspace
        SplitView {
            Layout.fillWidth: true
            Layout.fillHeight: true
            orientation: Qt.Horizontal
            handle: Rectangle {
                implicitWidth: 3
                color: SplitHandle.pressed ? palette.highlight
                    : SplitHandle.hovered ? Qt.alpha(palette.highlight, 0.55)
                    : palette.dark
                Rectangle {
                    anchors.horizontalCenter: parent.horizontalCenter
                    width: 1
                    height: parent.height
                    color: SplitHandle.pressed || SplitHandle.hovered ? palette.highlight : palette.mid
                }
            }

            LayersPanel {
                SplitView.preferredWidth: 280
                SplitView.minimumWidth: 200
            }

            SplitView {
                SplitView.fillWidth: true
                orientation: Qt.Horizontal
                handle: Rectangle {
                    implicitWidth: 3
                    color: SplitHandle.pressed ? palette.highlight
                        : SplitHandle.hovered ? Qt.alpha(palette.highlight, 0.55)
                        : palette.dark
                    Rectangle {
                        anchors.horizontalCenter: parent.horizontalCenter
                        width: 1
                        height: parent.height
                        color: SplitHandle.pressed || SplitHandle.hovered ? palette.highlight : palette.mid
                    }
                }
                Viewport3D {
                    id: viewport
                    onContextMenuRequested: contextMenu.popup()
                    onPinMenuRequested: pinMenu.popup()
                    // "--view wide3d" (automated captures) gives the 3D view all the room the other panels can spare.
                    SplitView.preferredWidth: App.startupView.indexOf("wide3d") >= 0 ? Math.max(520, window.width - 750) : 520
                    SplitView.minimumWidth: 200
                }
                PatternView {
                    id: patternView
                    onContextMenuRequested: contextMenu.popup()
                    SplitView.fillWidth: true
                    SplitView.minimumWidth: 200
                }
            }

            PropertiesPanel {
                id: propertiesPanel
                SplitView.preferredWidth: 330
                SplitView.minimumWidth: 260
                onPickColor: (current) => { colorDialog.selectedColor = current; colorDialog.open() }
                onPickTexture: textureDialog.open()
            }
        }

        // ------------------------------------------------------------------ status bar
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: 26
            color: palette.dark
            Rectangle {
                anchors { left: parent.left; right: parent.right; top: parent.top }
                height: 1
                color: palette.mid
            }
            RowLayout {
                anchors.fill: parent
                anchors.leftMargin: 10
                anchors.rightMargin: 10
                spacing: 14
                Label {
                    text: App.statusText
                    Layout.fillWidth: true
                    elide: Text.ElideRight
                    font.pixelSize: 11
                    color: palette.windowText
                    opacity: 0.85
                }
                Rectangle {
                    visible: App.simInfo !== ""
                    implicitWidth: simInfoLabel.implicitWidth + 14
                    implicitHeight: 18
                    radius: 4
                    color: Qt.alpha(App.themeInfoText, 0.14)
                    border.color: Qt.alpha(App.themeInfoText, 0.45)
                    border.width: 1
                    Label {
                        id: simInfoLabel
                        anchors.centerIn: parent
                        text: App.simInfo
                        font.pixelSize: 11
                        font.weight: Font.Medium
                        color: App.themeInfoText
                    }
                }
                Label {
                    font.pixelSize: 11
                    color: palette.placeholderText
                    text: propertiesPanel.sel.totalFaces !== undefined ? tr("%1 faces total").arg(propertiesPanel.sel.totalFaces.toLocaleString(Qt.locale(), "f", 0)) : ""
                }
            }
        }
    }

    // ------------------------------------------------------------------ dialogs
    FileDialog {
        id: openDialog
        title: tr("Open Garment")
        nameFilters: [tr("Seamstress garments") + " (*.garment.json *.json)", tr("All files") + " (*)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        onAccepted: App.open(selectedFile)
    }
    FileDialog {
        id: saveDialog
        title: tr("Save Garment")
        fileMode: FileDialog.SaveFile
        nameFilters: [tr("Seamstress garments") + " (*.garment.json)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        property var then: null
        onAccepted: { if (App.saveAs(selectedFile) && then) then() }
    }
    FileDialog {
        id: exportDialog
        title: tr("Export")
        fileMode: FileDialog.SaveFile
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        onAccepted: App.exportTo(window.pendingExport, selectedFile, weldBox.checked, scopeBox.currentIndex === 1)
    }
    FileDialog {
        id: svgDialog
        title: tr("Export Pattern as SVG")
        fileMode: FileDialog.SaveFile
        nameFilters: [tr("SVG drawing") + " (*.svg)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        onAccepted: App.exportPatternSvg(selectedFile, true, true)
    }
    FileDialog {
        id: imageDialog
        title: tr("Save 3D View as Image")
        fileMode: FileDialog.SaveFile
        nameFilters: [tr("PNG image") + " (*.png)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.PicturesLocation)
        onAccepted: viewport.saveImage(App.imagePath(selectedFile), imageOptions.multiple, transparentBox.checked)
    }
    FileDialog {
        id: textureDialog
        title: tr("Choose Texture")
        nameFilters: [tr("Images") + " (*.png *.jpg *.jpeg *.webp *.bmp)", tr("All files") + " (*)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.PicturesLocation)
        onAccepted: App.setMaterialTexture(selectedFile)
    }
    FileDialog {
        id: referenceDialog
        title: tr("Add Reference Image")
        nameFilters: [tr("Images") + " (*.png *.jpg *.jpeg *.gif *.webp *.bmp)", tr("All files") + " (*)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.PicturesLocation)
        onAccepted: patternView.addReferenceImage(selectedFile)
    }
    FileDialog {
        id: avatarDialog
        title: tr("Load Body (OBJ, FBX or glTF)")
        // OBJ files are taken to be in metres; FBX and glTF say what their units are.
        nameFilters: [tr("3D bodies") + " (" + App.avatarFilePatterns + ")", "Wavefront OBJ (*.obj)", "Autodesk FBX (*.fbx)",
                      "glTF (*.glb *.gltf)", tr("All files") + " (*)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        onAccepted: App.setAvatar("CustomObj", selectedFile, 1)
    }
    FileDialog {
        id: importMeshDialog
        title: tr("Import Mesh as Pattern (OBJ)")
        nameFilters: ["Wavefront OBJ (*.obj)"]
        currentFolder: StandardPaths.writableLocation(StandardPaths.DocumentsLocation)
        onAccepted: App.importMeshAsPattern(selectedFile)
    }
    ColorDialog {
        id: colorDialog
        title: tr("Fabric Colour")
        onSelectedColorChanged: if (visible) App.setMaterialColor(selectedColor.toString())
        onAccepted: App.setMaterialColor(selectedColor.toString())
    }

    AppDialog {
        id: exportOptions
        title: tr("Export %1").arg(window.pendingExport === "obj" ? "OBJ" : window.pendingExport === "fbx" ? "FBX" : "glTF")
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok | Dialog.Cancel
        ColumnLayout {
            spacing: 10
            RowLayout {
                Label { text: tr("Export") }
                ComboBox { id: scopeBox; implicitWidth: 240; model: [tr("All visible garments"), tr("Active garment only")] }
            }
            CheckBox {
                id: weldBox
                checked: true
                text: tr("Weld sewn seams into one connected mesh")
            }
            Label {
                Layout.preferredWidth: 380
                wrapMode: Text.WordWrap
                opacity: 0.65
                font.pixelSize: 11
                text: tr("Exports the garment as it currently looks in the 3D view, in metres with Y up. Set the face count with Particle distance in the Topology panel.")
            }
        }
        onAccepted: {
            const ext = window.pendingExport === "obj" ? ".obj" : window.pendingExport === "fbx" ? ".fbx" : ".glb"
            exportDialog.nameFilters = window.pendingExport === "obj" ? ["Wavefront OBJ (*.obj)"]
                                     : window.pendingExport === "fbx" ? ["Autodesk FBX (*.fbx)"]
                                     : [tr("glTF binary") + " (*.glb)", tr("glTF separate") + " (*.gltf)"]
            exportDialog.currentFile = exportDialog.currentFolder + "/" + App.suggestedFileName(ext)
            exportDialog.open()
        }
    }

    // File ▸ Save 3D View as Image: how large, and with or without the background.
    AppDialog {
        id: imageOptions
        title: tr("Save 3D View as Image")
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok | Dialog.Cancel
        okText: tr("Choose File…")
        readonly property var multiples: [1, 2, 3, 4]
        // Which of them is chosen; kept here because the list is rebuilt (and its choice reset) when the view is resized.
        property int choice: 1
        readonly property real multiple: multiples[choice]
        ColumnLayout {
            spacing: 10
            RowLayout {
                Label { text: tr("Size") }
                ComboBox {
                    id: sizeBox
                    implicitWidth: 300
                    onModelChanged: currentIndex = imageOptions.choice
                    Component.onCompleted: currentIndex = imageOptions.choice
                    onActivated: (index) => imageOptions.choice = index
                    // "2× the view (1040 × 1600 pixels)": the picture is rendered at that size, not scaled up.
                    model: imageOptions.multiples.map(m => {
                        const size = viewport.imageSize(m)
                        return tr("%1× the view (%2 × %3 pixels)").arg(m).arg(size.width).arg(size.height)
                    })
                }
            }
            CheckBox {
                id: transparentBox
                text: tr("Transparent background")
            }
            Label {
                Layout.preferredWidth: 400
                wrapMode: Text.WordWrap
                opacity: 0.65
                font.pixelSize: 11
                text: tr("Saves what the 3D view shows from where the camera is now, as a PNG, without the buttons and hints. With a transparent background the ground grid is left out too, so the picture can be placed on any backdrop.")
            }
        }
        onAccepted: {
            imageDialog.currentFile = imageDialog.currentFolder + "/" + App.suggestedFileName(".png")
            imageDialog.open()
        }
    }

    // Asked at startup when unsaved work of a session that did not close properly was found.
    AppDialog {
        id: recoveryDialog
        title: tr("Restore Unsaved Work?")
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok | Dialog.Discard | Dialog.Cancel
        okText: tr("Restore")
        discardText: tr("Delete Recovery Copy")
        closePolicy: Popup.CloseOnEscape
        // The copy being asked about ({key, name, file, saved}) and how many others wait behind it.
        property var copy: ({ key: "", name: "", file: "", saved: "" })
        property int others: 0
        function offer() {
            const copies = JSON.parse(App.recoveryCopies())
            if (copies.length === 0)
                return
            copy = copies[0]
            others = copies.length - 1
            open()
        }
        ColumnLayout {
            spacing: 8
            Label { text: tr("Seamstress did not close properly last time."); font.bold: true }
            Label {
                Layout.preferredWidth: 460
                wrapMode: Text.WordWrap
                text: recoveryDialog.copy.file !== ""
                    ? tr("A recovery copy of “%1” from %2 is newer than the saved project.").arg(App.fileName(recoveryDialog.copy.file)).arg(recoveryDialog.copy.saved)
                    : tr("A recovery copy of the unsaved project “%1” from %2 was found.").arg(recoveryDialog.copy.name).arg(recoveryDialog.copy.saved)
            }
            Label {
                visible: recoveryDialog.copy.file !== ""
                Layout.preferredWidth: 460
                elide: Text.ElideMiddle
                opacity: 0.7
                font.pixelSize: 11
                text: recoveryDialog.copy.file
            }
            Label {
                Layout.preferredWidth: 460
                wrapMode: Text.WordWrap
                opacity: 0.65
                font.pixelSize: 11
                text: tr("Restore opens it as unsaved changes: save to keep them, or undo to go back to the saved project. Cancel keeps the copy and asks again next time.")
            }
            Label {
                visible: recoveryDialog.others > 0
                Layout.preferredWidth: 460
                wrapMode: Text.WordWrap
                opacity: 0.65
                font.pixelSize: 11
                text: tr("%1 more recovery copies will be offered afterwards.").arg(recoveryDialog.others)
            }
        }
        onAccepted: App.restoreRecovery(copy.key)
        onDiscarded: {
            close()
            App.discardRecovery(copy.key)
            Qt.callLater(offer)
        }
    }
    Timer { interval: 600; running: App.recoveryPending; onTriggered: recoveryDialog.offer() }

    // Files dragged onto the window from the file manager.
    DropArea {
        id: dropArea
        anchors.fill: parent
        onEntered: (drag) => { drag.accepted = drag.hasUrls }
        onDropped: (drop) => {
            if (drop.hasUrls && window.openDropped(drop.urls) > 0)
                drop.accept()
        }
        Rectangle {
            anchors.fill: parent
            anchors.margins: 6
            visible: dropArea.containsDrag
            color: Qt.alpha(palette.highlight, 0.10)
            border.color: palette.highlight
            border.width: 2
            radius: 8
            Label {
                anchors.centerIn: parent
                padding: 14
                font.pixelSize: 15
                font.weight: Font.DemiBold
                text: tr("Drop a project to open it, a body (OBJ, FBX, glTF) to use as the avatar, or a picture to trace")
                background: Rectangle { color: Qt.alpha(palette.window, 0.92); radius: 6; border.color: palette.highlight; border.width: 1 }
            }
        }
    }

    AppDialog {
        id: makeHumanDialog
        title: tr("Send to MakeHuman")
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok | Dialog.Cancel
        // Where the asset goes and whether that replaces one already there, for the name being typed.
        property var target: ({ folder: "", exists: false })
        // Every garment ({id, name, zDepth, visible, active}) and the ids of those ticked for sending.
        property var garments: []
        property var ticked: []
        // z_depth by garment id, kept apart from the list so editing one does not rebuild the rows.
        property var depths: ({})
        // The name last suggested; while the field still shows it, ticking garments updates it.
        property string suggested: ""
        readonly property var sending: garments.filter(g => ticked.indexOf(g.id) >= 0)
        readonly property int zDepth: sending.reduce((z, g) => Math.max(z, depths[g.id]), 0)
        onSendingChanged: if (standardButton(Dialog.Ok)) standardButton(Dialog.Ok).enabled = sending.length > 0 || !App.makeHumanLinked
        function check() { target = JSON.parse(App.makeHumanAssetInfo(assetName.text)) }
        function suggest() {
            const name = sending.length === 1 ? sending[0].name : sending.length > 1 ? App.documentName() : App.activeGarmentName()
            if (assetName.text === suggested)
                assetName.text = name
            suggested = name
        }
        function tick(id, on) {
            ticked = on ? ticked.concat([id]) : ticked.filter(t => t !== id)
            suggest()
        }
        function setZDepth(id, value) {
            App.setMakeHumanZDepth(id, value)
            const d = Object.assign({}, depths)
            d[id] = value
            depths = d
        }
        onAboutToShow: {
            garments = JSON.parse(App.makeHumanGarmentsJson())
            const d = {}
            for (const g of garments)
                d[g.id] = g.zDepth
            depths = d
            // Everything shown is sent by default; hidden garments are listed unticked.
            ticked = garments.filter(g => g.visible).map(g => g.id)
            assetName.text = ""
            suggested = ""
            suggest()
            check()
            if (App.makeHumanLinked)
                assetName.forceActiveFocus()
        }
        okText: tr("Send")
        ColumnLayout {
            spacing: 10
            Label {
                visible: !App.makeHumanLinked
                Layout.preferredWidth: 460
                wrapMode: Text.WordWrap
                text: tr("MakeHuman is not linked yet. Choose its user data folder and base mesh in Preferences first.")
            }
            GridLayout {
                visible: App.makeHumanLinked
                columns: 2
                columnSpacing: 10
                rowSpacing: 8
                Label { text: tr("Asset name") }
                TextField {
                    id: assetName
                    Layout.preferredWidth: 340
                    selectByMouse: true
                    placeholderText: tr("the name MakeHuman lists it under")
                    onTextChanged: makeHumanDialog.check()
                    onAccepted: makeHumanDialog.accept()
                }
                Label { text: tr("Garments"); Layout.alignment: Qt.AlignTop; Layout.topMargin: 6 }
                ColumnLayout {
                    Layout.preferredWidth: 340
                    spacing: 2
                    RowLayout {
                        Label { text: tr("Send"); opacity: 0.65; font.pixelSize: 11; Layout.fillWidth: true }
                        Label { text: tr("z_depth"); opacity: 0.65; font.pixelSize: 11; Layout.preferredWidth: 110; horizontalAlignment: Text.AlignHCenter }
                    }
                    Repeater {
                        model: makeHumanDialog.garments
                        RowLayout {
                            required property var modelData
                            CheckBox {
                                Layout.fillWidth: true
                                text: modelData.name + (modelData.visible ? "" : " " + tr("(hidden)"))
                                checked: makeHumanDialog.ticked.indexOf(modelData.id) >= 0
                                onToggled: makeHumanDialog.tick(modelData.id, checked)
                            }
                            NumberField {
                                Layout.preferredWidth: 110
                                from: 0
                                to: App.maxMakeHumanZDepth
                                decimals: 0
                                step: 1
                                value: modelData.zDepth
                                onEdited: (v) => makeHumanDialog.setZDepth(modelData.id, Math.round(v))
                                ToolTip.visible: hovered
                                ToolTip.text: tr("The layer MakeHuman draws this garment in; higher is further out. 50 is the default.")
                            }
                        }
                    }
                    Label {
                        visible: makeHumanDialog.sending.length > 1
                        Layout.fillWidth: true
                        wrapMode: Text.WordWrap
                        opacity: 0.75
                        font.pixelSize: 11
                        text: tr("The ticked garments are sent as one asset with z_depth %1, the highest of theirs.").arg(makeHumanDialog.zDepth)
                    }
                }
                Label { text: tr("Saved in"); opacity: 0.75 }
                Label {
                    Layout.preferredWidth: 340
                    text: makeHumanDialog.target.folder
                    elide: Text.ElideMiddle
                    opacity: 0.75
                    ToolTip.visible: savedIn.hovered
                    ToolTip.text: text
                    HoverHandler { id: savedIn }
                }
                Label { text: tr("Author"); opacity: 0.75 }
                Label { text: App.settingAuthor !== "" ? App.settingAuthor : App.defaultAuthor; opacity: 0.75 }
                Label { text: tr("License"); opacity: 0.75 }
                Label { text: App.settingMakeHumanLicense; opacity: 0.75 }
            }
            Label {
                visible: App.makeHumanLinked && makeHumanDialog.target.exists
                Layout.preferredWidth: 460
                wrapMode: Text.WordWrap
                color: App.themeWarningText
                text: tr("An asset with this name is already there. Sending replaces it; choose another name to keep both.")
            }
            Label {
                visible: App.makeHumanLinked
                Layout.preferredWidth: 460
                wrapMode: Text.WordWrap
                opacity: 0.65
                font.pixelSize: 11
                text: tr("The garments are fitted to the MakeHuman base mesh, so they follow the character when you change body shape. In MakeHuman the asset appears under Geometries ▸ Clothes. Author and license are set in Preferences ▸ MakeHuman.")
            }
            Label {
                visible: App.makeHumanLinked && App.avatarType !== "MakeHuman"
                Layout.preferredWidth: 460
                wrapMode: Text.WordWrap
                color: App.themeWarningText
                text: tr("The avatar is not the MakeHuman base mesh. Switch to Avatar ▸ MakeHuman Base Mesh and simulate the garment on it before sending.")
            }
            RowLayout {
                Button { visible: !App.makeHumanLinked; text: tr("Open Preferences…"); onClicked: { makeHumanDialog.close(); preferences.openPage("MakeHuman") } }
                Button { visible: App.makeHumanLinked; text: tr("Change Author or License…"); flat: true; onClicked: { makeHumanDialog.close(); preferences.openPage("MakeHuman") } }
            }
        }
        onAccepted: if (App.makeHumanLinked) App.sendToMakeHuman(assetName.text, ticked.join("|"))
    }

    AppDialog {
        id: errorDialog
        property alias text: errorText.text
        title: "Seamstress"
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok
        ColumnLayout {
            Label { id: errorText; wrapMode: Text.WordWrap; Layout.maximumWidth: 520 }
        }
    }
    AppDialog {
        id: unsavedDialog
        title: tr("Unsaved Changes")
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Save | Dialog.Discard | Dialog.Cancel
        ColumnLayout {
            spacing: 6
            Label { text: tr("This garment has unsaved changes."); font.bold: true }
            Label { text: tr("Do you want to save them first?") }
        }
        function finish(saving) {
            const action = window.afterDiscard
            window.afterDiscard = null
            if (saving)
                window.save(action)
            else if (action)
                action()
        }
        onAccepted: finish(true)
        onDiscarded: { close(); finish(false) }
        onRejected: window.afterDiscard = null
    }
    AppDialog {
        id: aboutDialog
        title: tr("About Seamstress")
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok
        ColumnLayout {
            spacing: 8
            Label { text: "Seamstress " + App.version; font.bold: true; font.pixelSize: 15 }
            Label {
                Layout.maximumWidth: 460
                wrapMode: Text.WordWrap
                text: tr("A 3D garment creation tool: draw pattern pieces, sew them, and drape them on an avatar.") + "\n\n"
                      + tr("Built with C# and Qt Quick (Qt Bridge for C#).") + "\n" + tr("3D viewport: %1").arg(App.renderer)
            }
            Label {
                Layout.maximumWidth: 460
                wrapMode: Text.WordWrap
                font.pixelSize: 11
                opacity: 0.7
                text: tr("Source code: MIT licence. This build includes Qt Quick 3D under the GPL-3.0, so the package as a whole is GPL-3.0; the licence texts and notices are in LICENSES/ and NOTICE.")
            }
        }
    }

    PreferencesDialog { id: preferences }
    EditMenu { id: contextMenu }
    PinMenu { id: pinMenu }
    BodyShapeDialog { id: bodyShape }
}
