import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// Simulation quality beside the Simulate button: Draft (a coarse working mesh for building big outfits) or Final (each
// garment's own particle distance). At Draft the second half offers "Refine to Final".
RowLayout {
    id: root
    spacing: 0
    function tr(text) { App.languageRevision; return App.tr(text) }
    function cm(value) {
        return App.imperial ? (value / 2.54).toLocaleString(Qt.locale(), "f", 2) + " in"
                            : value.toLocaleString(Qt.locale(), "f", 1) + " cm"
    }

    component Half: Button {
        id: half
        property bool on: false
        property color accent: "#f59e0b"
        implicitHeight: 26
        hoverEnabled: true
        font.pixelSize: 11
        leftPadding: 9; rightPadding: 9
        contentItem: Label {
            text: half.text
            font: half.font
            color: half.on ? half.accent : palette.windowText
            opacity: half.on || half.hovered ? 1 : 0.7
            horizontalAlignment: Text.AlignHCenter
            verticalAlignment: Text.AlignVCenter
        }
        background: Rectangle {
            radius: 5
            color: half.on ? Qt.alpha(half.accent, 0.18)
                 : half.hovered ? Qt.alpha(palette.windowText, 0.08) : "transparent"
            border.width: 1
            border.color: half.on ? Qt.alpha(half.accent, 0.55) : Qt.alpha(palette.windowText, 0.18)
        }
        ToolTip.visible: hovered
        ToolTip.delay: 450
    }

    Half {
        objectName: "draftQualityButton"
        text: root.tr("Draft")
        on: App.draftQuality
        accent: "#f59e0b"
        onClicked: App.setDraftQuality(true)
        ToolTip.text: App.draftQuality
            ? root.tr("Draft quality: garments are meshed no finer than %1, so big outfits simulate several times faster. Drapes and pins come along when you refine.").arg(root.cm(App.draftParticleDistance))
            : root.tr("Switch to Draft: mesh every garment no finer than %1 while you build and arrange, for a much faster simulation. Drapes and pins are carried over.").arg(root.cm(App.draftParticleDistance))
    }
    Half {
        objectName: "finalQualityButton"
        text: App.draftQuality ? root.tr("Refine to Final") : root.tr("Final")
        on: !App.draftQuality
        accent: "#10b981"
        onClicked: App.setDraftQuality(false)
        ToolTip.text: App.draftQuality
            ? root.tr("Refine to Final: re-mesh every garment at its own particle distance, keeping the drape and pins. Creases come out softer, so set them again where fine folds matter.")
            : root.tr("Final quality: each garment is simulated at its own particle distance.")
    }
}
