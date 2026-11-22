import QtQuick
import QtQuick.Controls

// Right-click menu of the 3D pin brush. The Simulation menu in Main.qml lists the same pin actions (all but "Turn Off
// Pin Brush", which the View menu and the Pin button have); keep the two in step.
Menu {
    function tr(text) { App.languageRevision; return App.tr(text) }
    MenuItem {
        text: tr("Pin Selected Vertices")
        enabled: App.pinSelectionCount > 0
        onTriggered: App.pinSelectedVertices()
    }
    MenuItem {
        text: tr("Unpin Selected Vertices")
        enabled: App.pinSelectionCount > 0
        onTriggered: App.unpinSelectedVertices()
    }
    MenuItem {
        text: tr("Clear Vertex Selection")
        enabled: App.pinSelectionCount > 0
        onTriggered: App.clearPinSelection()
    }
    MenuSeparator {}
    MenuItem {
        text: tr("Unpin All on Selected Garments")
        onTriggered: App.unpinAll()
    }
    MenuSeparator {}
    MenuItem {
        text: tr("Turn Off Pin Brush")
        onTriggered: App.pinBrush = false
    }
}
