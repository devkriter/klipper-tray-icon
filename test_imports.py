try:
    import pystray
    import PIL
    import requests
    print("Imports successful.")
except ImportError as e:
    print(f"Import failed: {e}")
    exit(1)

print("Test complete.")
exit(0)
