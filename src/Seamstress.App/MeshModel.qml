import QtQuick
import QtQuick3D
import QtQuick3D.Helpers

// A model whose mesh comes from App.viewportModels(): either a ".mesh" file's URL or a "live:" key. For a key, the
// C# side writes the vertex buffers straight into this model's geometry (LiveGeometry.cs) whenever they change, so a
// moving garment never goes through a file and nothing in QML has to change for the picture to follow it.
Model {
    id: meshModel

    property string mesh: ""
    readonly property bool live: mesh.startsWith("live:")
    // The geometry the buffers go into; another model (the back faces of the normals view) can show it too.
    property alias liveGeometry: liveMesh

    source: live ? "" : mesh
    geometry: live ? liveMesh : null

    // Filled from C#: its properties are never set here, so it never rebuilds itself over what C# put in.
    ProceduralMesh { id: liveMesh }

    property string attachedKey: ""
    function attachLive() {
        const key = live ? mesh : ""
        if (key === attachedKey)
            return
        if (attachedKey !== "")
            App.detachGeometry(attachedKey, String(liveMesh))
        attachedKey = key
        if (key !== "")
            App.attachGeometry(key, String(liveMesh))
    }
    onMeshChanged: attachLive()
    Component.onCompleted: attachLive()
    Component.onDestruction: {
        if (attachedKey !== "")
            App.detachGeometry(attachedKey, String(liveMesh))
    }
}
