let dotNetRef = null;

window.addModalKeyboardListener = (dotNetObject) => {
    dotNetRef = dotNetObject;
    document.addEventListener('keydown', handleKeyDown);
};

window.removeModalKeyboardListener = () => {
    document.removeEventListener('keydown', handleKeyDown);
    dotNetRef = null;
};

function handleKeyDown(event) {
    if (!dotNetRef) return;
    
    switch (event.key) {
        case 'ArrowLeft':
        case 'ArrowRight':
        case 'Escape':
            event.preventDefault();
            dotNetRef.invokeMethodAsync('HandleKeyPress', event.key);
            break;
    }
}

// Get the base URL for SignalR connections
window.getBaseUrl = () => {
    return document.baseURI || window.location.origin + '/';
}; 