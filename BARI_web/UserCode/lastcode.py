print("Hola desde main.py (MicroPython)!")
import machine, time
led = machine.Pin(13, machine.Pin.OUT)

while True:
    led.value(0)
